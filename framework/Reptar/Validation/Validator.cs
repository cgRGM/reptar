using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Reptar;

public interface IValidatorMeta
{
    IReadOnlyList<PropertyRuleMeta> Properties { get; }
}

public sealed class PropertyRuleMeta
{
    public required string Name { get; init; }
    public required Type PropertyType { get; init; }
    public bool IsOptional { get; internal set; }
    public int? MinLength { get; internal set; }
    public int? MaxLength { get; internal set; }
    public int? ExactLength { get; internal set; }
    public double? MinValue { get; internal set; }
    public double? MaxValue { get; internal set; }
    public bool IsEmail { get; internal set; }
    public bool IsUrl { get; internal set; }
    public string? Pattern { get; internal set; }
}

public sealed class Validator<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T> : IValidatorMeta
{
    private readonly List<IPropertyRule> _properties = new();
    private readonly List<RefineRule> _refines = new();
    private readonly List<PropertyRuleMeta> _propertyMetas = new();

    public IReadOnlyList<PropertyRuleMeta> Properties => _propertyMetas;

    public Validator<T> Prop<TProp>(
        Expression<Func<T, TProp>> property,
        Action<PropertyRules> configure)
    {
        var body = property.Body is UnaryExpression
        {
            NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
        } unary
            ? unary.Operand
            : property.Body;

        if (body is not MemberExpression { Member: PropertyInfo propertyInfo })
            throw new NotSupportedException(
                "Prop() expects a property expression like x => x.Title.");

        var name = Camelize(propertyInfo.Name);
        var meta = new PropertyRuleMeta
        {
            Name = name,
            PropertyType = propertyInfo.PropertyType,
        };

        var rules = PropertyRules.Create(propertyInfo.PropertyType, meta);
        configure(rules);

#if NETWASM
        var compiled = property.Compile();
#else
        Func<T, TProp> compiled;

        try
        {
            // Prefer real IL when the runtime supports it (Kestrel/JIT): the
            // interpreted tree allocates per call. Native AOT binds the
            // property accessor directly when it can; reflection is the last
            // resort.
            compiled = RuntimeFeature.IsDynamicCodeSupported
                ? property.Compile()
                : CreateAotGetter<TProp>(propertyInfo);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
        {
            compiled = CreateAotGetter<TProp>(propertyInfo);
        }
#endif

        _properties.Add(new PropertyRule<TProp>(
            name,
            value => value is null ? default! : compiled(value),
            rules));

        _propertyMetas.Add(meta);

        return this;
    }

#if !NETWASM
    private static Func<T, TProp> CreateAotGetter<TProp>(PropertyInfo propertyInfo)
    {
        try
        {
            // A direct delegate avoids per-call reflection and boxing when the
            // AOT runtime can bind the accessor. Value-type receivers and
            // runtimes that reject CreateDelegate fall back to GetValue.
            var getter = propertyInfo.GetMethod;
            if (getter is not null)
                return getter.CreateDelegate<Func<T, TProp>>();
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // Fall through to reflection.
        }

        return value => value is null
            ? default!
            : (TProp)propertyInfo.GetValue(value)!;
    }
#endif

    public Validator<T> Prop<TProp>(
        string name,
        Func<T, TProp> getter,
        Action<PropertyRules> configure)
    {
        var propName = Camelize(name);
        var meta = new PropertyRuleMeta
        {
            Name = propName,
            PropertyType = typeof(TProp),
        };

        var rules = PropertyRules.Create(typeof(TProp), meta);
        configure(rules);

        _properties.Add(new PropertyRule<TProp>(
            propName,
            value => value is null ? default! : getter(value),
            rules));

        _propertyMetas.Add(meta);

        return this;
    }

    public Validator<T> Refine(Func<T, bool> test, string message, string? path = null)
    {
        _refines.Add(new RefineRule(test, message, path is null ? null : [path]));
        return this;
    }

    public IReadOnlyList<ValidationIssue> Validate(T value)
    {
        List<ValidationIssue>? issues = null;

        foreach (var property in _properties)
            property.Check(value, ref issues);

        foreach (var refine in _refines)
        {
            if (!refine.Test(value))
                PropertyRules.AddIssue(ref issues, refine.Message, refine.Path);
        }

        return issues is null ? Array.Empty<ValidationIssue>() : issues;
    }

    private static string Camelize(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private interface IPropertyRule
    {
        void Check(T value, ref List<ValidationIssue>? issues);
    }

    private sealed class PropertyRule<TProp>(
        string path,
        Func<T, TProp> getter,
        PropertyRules rules) : IPropertyRule
    {
        public void Check(T value, ref List<ValidationIssue>? issues) =>
            rules.Check(getter(value), path, ref issues);
    }

    private sealed record RefineRule(Func<T, bool> Test, string Message, string[]? Path);
}

public abstract class PropertyRules
{
    protected readonly PropertyRuleMeta? Meta;

    protected PropertyRules(PropertyRuleMeta? meta = null)
    {
        Meta = meta;
    }

    protected bool IsOptional { get; private set; }

    public abstract PropertyRules Optional();

    public virtual PropertyRules Min(int value) => this;
    public virtual PropertyRules Max(int value) => this;
    public virtual PropertyRules Length(int value) => this;
#if !NETWASM
    public virtual PropertyRules Regex(string pattern) => this;
#endif
    public virtual PropertyRules Email() => this;
    public virtual PropertyRules Url() => this;
    public virtual PropertyRules NotNull() => this;

    internal abstract void Check(
        object? value,
        string path,
        ref List<ValidationIssue>? issues);

    /// <summary>
    /// Typed fast path used by <see cref="Validator{T}"/>; the default falls
    /// back to the object overload for uncommon property types.
    /// </summary>
    internal virtual void Check<TProp>(
        TProp value,
        string path,
        ref List<ValidationIssue>? issues) =>
        Check((object?)value, path, ref issues);

    internal static void AddIssue(
        ref List<ValidationIssue>? issues,
        string message,
        string[]? path) =>
        (issues ??= new List<ValidationIssue>(1))
            .Add(new ValidationIssue(message, path));

    internal static PropertyRules Create(Type type, PropertyRuleMeta? meta = null)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(string))
            return new StringRules(meta);

        if (target == typeof(int) || target == typeof(long) || target == typeof(short) ||
            target == typeof(double) || target == typeof(decimal) || target == typeof(float))
            return new NumberRules(meta);

        return new BasicRules(meta);
    }

    protected void MarkOptional()
    {
        IsOptional = true;
        if (Meta is not null)
            Meta.IsOptional = true;
    }

    protected bool ShouldRequire => !IsOptional;
}

public sealed class StringRules : PropertyRules
{
    private int? _min;
    private int? _max;
    private int? _length;
#if !NETWASM
    private string? _pattern;
#endif
    private bool _email;
    private bool _url;

    public StringRules(PropertyRuleMeta? meta = null) : base(meta) { }

    public override StringRules Min(int value)
    {
        _min = value;
        if (Meta is not null)
            Meta.MinLength = value;
        return this;
    }

    public override StringRules Max(int value)
    {
        _max = value;
        if (Meta is not null)
            Meta.MaxLength = value;
        return this;
    }

    public override StringRules Length(int value)
    {
        _length = value;
        if (Meta is not null)
            Meta.ExactLength = value;
        return this;
    }

#if !NETWASM
    public override StringRules Regex(string pattern)
    {
        _pattern = pattern;
        if (Meta is not null)
            Meta.Pattern = pattern;
        return this;
    }
#endif

    public override StringRules Email()
    {
        _email = true;
        if (Meta is not null)
            Meta.IsEmail = true;
        return this;
    }

    public override StringRules Url()
    {
        _url = true;
        if (Meta is not null)
            Meta.IsUrl = true;
        return this;
    }

    public override StringRules NotNull() => this;

    public override PropertyRules Optional()
    {
        MarkOptional();
        return this;
    }

    internal override void Check<TProp>(
        TProp value,
        string path,
        ref List<ValidationIssue>? issues)
    {
        if (value is string text)
        {
            CheckText(text, path, ref issues);
            return;
        }

        Check((object?)value, path, ref issues);
    }

    internal override void Check(
        object? value,
        string path,
        ref List<ValidationIssue>? issues)
    {
        if (value is null)
        {
            if (ShouldRequire)
                AddIssue(ref issues, "Required.", [path]);

            return;
        }

        CheckText((string)value, path, ref issues);
    }

    private void CheckText(
        string text,
        string path,
        ref List<ValidationIssue>? issues)
    {
        if (_min is { } min && text.Length < min)
            AddIssue(ref issues, $"Must be at least {min} characters.", [path]);

        if (_max is { } max && text.Length > max)
            AddIssue(ref issues, $"Must be at most {max} characters.", [path]);

        if (_length is { } length && text.Length != length)
            AddIssue(ref issues, $"Must be exactly {length} characters.", [path]);

#if !NETWASM
        if (_pattern is not null &&
            !System.Text.RegularExpressions.Regex.IsMatch(text, _pattern))
        {
            AddIssue(ref issues, "Invalid format.", [path]);
        }
#endif

        if (_email && !IsEmail(text))
            AddIssue(ref issues, "Must be a valid email.", [path]);

        if (_url && !Uri.TryCreate(text, UriKind.Absolute, out _))
            AddIssue(ref issues, "Must be a valid URL.", [path]);
    }

    private static bool IsEmail(string text)
    {
        var at = text.IndexOf('@');

        if (at <= 0 || at != text.LastIndexOf('@') || at == text.Length - 1)
            return false;

        var dot = text.LastIndexOf('.');
        return dot > at + 1 &&
            dot < text.Length - 1 &&
            !ContainsBlockedEmailChar(text);
    }

    // Rejects whitespace, control characters, non-ASCII, and the angle
    // brackets and quotes used to smuggle email-looking strings into HTML or
    // header contexts (a@b.c"><script>). Use Refine() for internationalized
    // addresses that need broader rules.
    private static bool ContainsBlockedEmailChar(string text)
    {
        foreach (var c in text)
        {
            if (c <= ' ' ||
                c > (char)126 ||
                c is '<' or '>' or '"' or '\'' or '\\')
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class NumberRules : PropertyRules
{
    private double? _min;
    private double? _max;

    public NumberRules(PropertyRuleMeta? meta = null) : base(meta) { }

    public override NumberRules Min(int value)
    {
        _min = value;
        if (Meta is not null)
            Meta.MinValue = value;
        return this;
    }

    public override NumberRules Max(int value)
    {
        _max = value;
        if (Meta is not null)
            Meta.MaxValue = value;
        return this;
    }

    public override NumberRules NotNull() => this;

    public override PropertyRules Optional()
    {
        MarkOptional();
        return this;
    }

    internal override void Check<TProp>(
        TProp value,
        string path,
        ref List<ValidationIssue>? issues)
    {
        double number;

        if (value is int intValue)
            number = intValue;
        else if (value is long longValue)
            number = longValue;
        else if (value is short shortValue)
            number = shortValue;
        else if (value is double doubleValue)
            number = doubleValue;
        else if (value is float floatValue)
            number = floatValue;
        else if (value is decimal decimalValue)
            number = (double)decimalValue;
        else
        {
            Check((object?)value, path, ref issues);
            return;
        }

        CheckNumber(number, path, ref issues);
    }

    internal override void Check(
        object? value,
        string path,
        ref List<ValidationIssue>? issues)
    {
        if (value is null)
        {
            if (ShouldRequire)
                AddIssue(ref issues, "Required.", [path]);

            return;
        }

        CheckNumber(Convert.ToDouble(value), path, ref issues);
    }

    private void CheckNumber(
        double number,
        string path,
        ref List<ValidationIssue>? issues)
    {
        if (_min is { } min && number < min)
            AddIssue(ref issues, $"Must be at least {min}.", [path]);

        if (_max is { } max && number > max)
            AddIssue(ref issues, $"Must be at most {max}.", [path]);
    }
}

public sealed class BasicRules : PropertyRules
{
    public BasicRules(PropertyRuleMeta? meta = null) : base(meta) { }

    public override BasicRules NotNull() => this;

    public override PropertyRules Optional()
    {
        MarkOptional();
        return this;
    }

    internal override void Check<TProp>(
        TProp value,
        string path,
        ref List<ValidationIssue>? issues)
    {
        if (value is null && ShouldRequire)
            AddIssue(ref issues, "Required.", [path]);
    }

    internal override void Check(
        object? value,
        string path,
        ref List<ValidationIssue>? issues)
    {
        if (value is null && ShouldRequire)
            AddIssue(ref issues, "Required.", [path]);
    }
}
