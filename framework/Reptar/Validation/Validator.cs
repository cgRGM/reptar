using System.Linq.Expressions;
using System.Reflection;

namespace Reptar;

public sealed class Validator<T>
{
    private readonly List<PropertyRule> _properties = new();
    private readonly List<RefineRule> _refines = new();

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

        var rules = PropertyRules.Create(propertyInfo.PropertyType);
        configure(rules);

        _properties.Add(new PropertyRule(
            Camelize(propertyInfo.Name),
            property.Compile(),
            rules));

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
    protected bool IsOptional { get; private set; }

    public abstract PropertyRules Optional();

    public virtual PropertyRules Min(int value) => this;
    public virtual PropertyRules Max(int value) => this;
    public virtual PropertyRules Length(int value) => this;
    public virtual PropertyRules Regex(string pattern) => this;
    public virtual PropertyRules Email() => this;
    public virtual PropertyRules Url() => this;
    public virtual PropertyRules NotNull() => this;

    internal abstract List<ValidationIssue> Check(object? value, string path);

    internal static PropertyRules Create(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(string))
            return new StringRules();

        if (target == typeof(int) || target == typeof(long) || target == typeof(short) ||
            target == typeof(double) || target == typeof(decimal) || target == typeof(float))
            return new NumberRules();

        return new BasicRules();
    }

    protected void MarkOptional() => IsOptional = true;

    protected bool ShouldRequire => !IsOptional;
}

public sealed class StringRules : PropertyRules
{
    private static readonly System.Text.RegularExpressions.Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private int? _min;
    private int? _max;
    private int? _length;
    private string? _pattern;
    private bool _email;
    private bool _url;

    public override StringRules Min(int value) { _min = value; return this; }
    public override StringRules Max(int value) { _max = value; return this; }
    public override StringRules Length(int value) { _length = value; return this; }
    public override StringRules Regex(string pattern) { _pattern = pattern; return this; }
    public override StringRules Email() { _email = true; return this; }
    public override StringRules Url() { _url = true; return this; }
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

        if (_pattern is not null &&
            !System.Text.RegularExpressions.Regex.IsMatch(text, _pattern))
        {
            issues.Add(new ValidationIssue("Invalid format.", [path]));
        }

        if (_email && !EmailPattern.IsMatch(text))
            issues.Add(new ValidationIssue("Must be a valid email.", [path]));

        if (_url && !Uri.TryCreate(text, UriKind.Absolute, out _))
            issues.Add(new ValidationIssue("Must be a valid URL.", [path]));

        return issues;
    }
}

public sealed class NumberRules : PropertyRules
{
    private double? _min;
    private double? _max;

    public override NumberRules Min(int value) { _min = value; return this; }
    public override NumberRules Max(int value) { _max = value; return this; }
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
