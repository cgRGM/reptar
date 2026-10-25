using System.Linq.Expressions;
using System.Reflection;

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

public sealed class Validator<T> : IValidatorMeta
{
    private readonly List<PropertyRule> _properties = new();
    private readonly List<RefineRule> _refines = new();
    private readonly List<PropertyRuleMeta> _propertyMetas = new();

    public IReadOnlyList<PropertyRuleMeta> Properties => _propertyMetas;

    public Validator<T> Prop(
        Expression<Func<T, object?>> property,
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
        Func<T, object?> getter = property.Compile();
#else
        Func<T, object?> getter = value => value is null ? null : propertyInfo.GetValue(value);
#endif

        _properties.Add(new PropertyRule(
            name,
            getter,
            rules));

        _propertyMetas.Add(meta);

        return this;
    }

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

        _properties.Add(new PropertyRule(
            propName,
            value => value is null ? null : getter(value),
            rules));

        _propertyMetas.Add(meta);

        return this;
    }

    public Validator<T> Refine(Func<T, bool> test, string message, string? path = null)
    {
        _refines.Add(new RefineRule(test, message, path is null ? null : [path]));
        return this;
    }

    public List<ValidationIssue> Validate(T value)
    {
        var issues = new List<ValidationIssue>();

        foreach (var property in _properties)
            issues.AddRange(property.Check(value));

        foreach (var refine in _refines)
        {
            if (!refine.Test(value))
                issues.Add(new ValidationIssue(refine.Message, refine.Path));
        }

        return issues;
    }

    private static string Camelize(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private sealed class PropertyRule(
        string path,
        Func<T, object?> getter,
        PropertyRules rules)
    {
        public List<ValidationIssue> Check(T value) => rules.Check(getter(value), path);
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

    internal abstract List<ValidationIssue> Check(object? value, string path);

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

    internal override List<ValidationIssue> Check(object? value, string path)
    {
        var issues = new List<ValidationIssue>();

        if (value is null)
        {
            if (ShouldRequire)
                issues.Add(new ValidationIssue("Required.", [path]));

            return issues;
        }

        var text = (string)value;

        if (_min is { } min && text.Length < min)
            issues.Add(new ValidationIssue($"Must be at least {min} characters.", [path]));

        if (_max is { } max && text.Length > max)
            issues.Add(new ValidationIssue($"Must be at most {max} characters.", [path]));

        if (_length is { } length && text.Length != length)
            issues.Add(new ValidationIssue($"Must be exactly {length} characters.", [path]));

#if !NETWASM
        if (_pattern is not null &&
            !System.Text.RegularExpressions.Regex.IsMatch(text, _pattern))
        {
            issues.Add(new ValidationIssue("Invalid format.", [path]));
        }
#endif

        if (_email && !IsEmail(text))
            issues.Add(new ValidationIssue("Must be a valid email.", [path]));

        if (_url && !Uri.TryCreate(text, UriKind.Absolute, out _))
            issues.Add(new ValidationIssue("Must be a valid URL.", [path]));

        return issues;
    }

    private static bool IsEmail(string text)
    {
        var at = text.IndexOf('@');

        if (at <= 0 || at != text.LastIndexOf('@') || at == text.Length - 1)
            return false;

        var dot = text.LastIndexOf('.');
        return dot > at + 1 &&
            dot < text.Length - 1 &&
            !text.Contains(' ') &&
            !text.Contains('\t');
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

    internal override List<ValidationIssue> Check(object? value, string path)
    {
        var issues = new List<ValidationIssue>();

        if (value is null)
        {
            if (ShouldRequire)
                issues.Add(new ValidationIssue("Required.", [path]));

            return issues;
        }

        var number = Convert.ToDouble(value);

        if (_min is { } min && number < min)
            issues.Add(new ValidationIssue($"Must be at least {min}.", [path]));

        if (_max is { } max && number > max)
            issues.Add(new ValidationIssue($"Must be at most {max}.", [path]));

        return issues;
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

    internal override List<ValidationIssue> Check(object? value, string path)
    {
        if (value is null && ShouldRequire)
            return [new ValidationIssue("Required.", [path])];

        return [];
    }
}
