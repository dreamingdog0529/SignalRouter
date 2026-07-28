using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SignalRouter.ApiSurface.Tests;

/// <summary>
/// Renders an assembly's exported surface as deterministic text: type kind and
/// modifiers (sealed / abstract / static / readonly / ref struct), base type,
/// implemented interfaces, generic parameters with constraints and variance,
/// constructors, methods, operators, properties with accessors, fields with
/// constant values, events, parameter modifiers (ref / out / in / params),
/// optional-parameter defaults, and nullable reference annotations. Everything
/// is sorted ordinally so the output is stable across runtimes and reflection
/// orderings; the checked-in baselines make any surface change a reviewable
/// diff (the performance-track safety net, plan P0a).
/// </summary>
internal static class ApiSurfaceRenderer
{
    internal static string Render(Assembly assembly)
    {
        var builder = new StringBuilder();
        builder.Append("# API surface of ").Append(assembly.GetName().Name).Append('\n');
        var types = assembly.GetExportedTypes()
            .OrderBy(type => type.FullName, StringComparer.Ordinal);
        foreach (var type in types)
        {
            RenderType(builder, type);
        }

        return builder.ToString();
    }

    private static void RenderType(StringBuilder builder, Type type)
    {
        builder.Append('\n').Append(TypeHeader(type)).Append('\n');

        if (type.IsEnum)
        {
            builder.Append("  : ").Append(FriendlyName(Enum.GetUnderlyingType(type))).Append('\n');
            var names = Enum.GetNames(type).OrderBy(name => name, StringComparer.Ordinal);
            foreach (var name in names)
            {
                var value = Convert.ToInt64(Enum.Parse(type, name), CultureInfo.InvariantCulture);
                builder.Append("  ").Append(name).Append(" = ")
                    .Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            return;
        }

        if (typeof(Delegate).IsAssignableFrom(type) && type != typeof(Delegate) && type != typeof(MulticastDelegate))
        {
            var invoke = type.GetMethod("Invoke");
            if (invoke != null)
            {
                builder.Append("  invoke ").Append(RenderMethodSignature(invoke)).Append('\n');
            }

            return;
        }

        foreach (var line in BaseAndInterfaceLines(type))
        {
            builder.Append("  ").Append(line).Append('\n');
        }

        foreach (var line in MemberLines(type))
        {
            builder.Append("  ").Append(line).Append('\n');
        }
    }

    private static string TypeHeader(Type type)
    {
        var parts = new List<string>();
        if (type.IsEnum)
        {
            parts.Add("enum");
        }
        else if (type.IsValueType)
        {
            if (type.IsByRefLike)
            {
                parts.Add("ref");
            }

            if (type.GetCustomAttributesData().Any(attribute =>
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute"))
            {
                parts.Add("readonly");
            }

            parts.Add("struct");
        }
        else if (type.IsInterface)
        {
            parts.Add("interface");
        }
        else if (typeof(Delegate).IsAssignableFrom(type))
        {
            parts.Add("delegate");
        }
        else
        {
            if (type.IsAbstract && type.IsSealed)
            {
                parts.Add("static");
            }
            else
            {
                if (type.IsAbstract)
                {
                    parts.Add("abstract");
                }

                if (type.IsSealed)
                {
                    parts.Add("sealed");
                }
            }

            parts.Add("class");
        }

        var header = string.Join(" ", parts) + " " + FriendlyName(type);
        var constraints = GenericConstraintLines(type.GetGenericArguments());
        return constraints.Length == 0 ? header : header + " " + constraints;
    }

    private static IEnumerable<string> BaseAndInterfaceLines(Type type)
    {
        var lines = new List<string>();
        if (type.IsClass && type.BaseType != null && type.BaseType != typeof(object))
        {
            lines.Add(": " + FriendlyName(type.BaseType));
        }

        // Only the interfaces the type itself claims (not ones inherited via the
        // base class) are part of its own declared surface — but reflection cannot
        // distinguish re-implementation, so the full transitive set is pinned:
        // any change to it is still a surface change worth reviewing.
        var interfaces = type.GetInterfaces()
            .Where(candidate => candidate.IsPublic || candidate.IsNestedPublic)
            .Select(FriendlyName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (interfaces.Length > 0)
        {
            lines.Add("implements " + string.Join(", ", interfaces));
        }

        return lines;
    }

    private static IEnumerable<string> MemberLines(Type type)
    {
        const BindingFlags declared =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var lines = new List<string>();

        foreach (var constructor in type.GetConstructors(declared).Where(IsSurface))
        {
            lines.Add(Accessibility(constructor) + " ctor(" + RenderParameters(constructor.GetParameters()) + ")");
        }

        foreach (var method in type.GetMethods(declared).Where(method => IsSurface(method) && !method.IsSpecialName))
        {
            lines.Add(
                Accessibility(method) + " " +
                (method.IsStatic ? "static " : "") +
                SlotModifier(type, method) +
                "method " + RenderMethodSignature(method));
        }

        // Operators and conversions are SpecialName static methods (op_*).
        foreach (var method in type.GetMethods(declared)
                     .Where(method => IsSurface(method) && method.IsSpecialName &&
                         method.Name.StartsWith("op_", StringComparison.Ordinal)))
        {
            lines.Add(Accessibility(method) + " operator " + RenderMethodSignature(method));
        }

        foreach (var property in type.GetProperties(declared))
        {
            var getter = property.GetMethod;
            var setter = property.SetMethod;
            var surfaceGet = getter != null && IsSurface(getter);
            var surfaceSet = setter != null && IsSurface(setter);
            if (!surfaceGet && !surfaceSet)
            {
                continue;
            }

            // Accessibility is rendered per accessor (a public getter with a
            // protected setter is a real shape) and the virtual-slot semantics
            // come from whichever accessor is on the surface.
            var accessors = new List<string>();
            if (surfaceGet)
            {
                accessors.Add(Accessibility(getter!) + " get");
            }

            if (surfaceSet)
            {
                accessors.Add(Accessibility(setter!) + " " + (IsInitOnly(setter!) ? "init" : "set"));
            }

            var indexParameters = property.GetIndexParameters();
            var name = indexParameters.Length > 0
                ? "this[" + RenderParameters(indexParameters) + "]"
                : property.Name;
            var slotSource = surfaceGet ? getter! : setter!;
            lines.Add(
                (slotSource.IsStatic ? "static " : "") +
                SlotModifier(type, slotSource) +
                "property " + name + " : " +
                RenderNullable(new NullabilityInfoContext().Create(property)) +
                " { " + string.Join("; ", accessors) + "; }");
        }

        foreach (var field in type.GetFields(declared).Where(IsSurface))
        {
            var prefix = field.IsLiteral ? "const "
                : field.IsStatic ? field.IsInitOnly ? "static readonly " : "static "
                : field.IsInitOnly ? "readonly " : "";
            var line = Accessibility(field) + " " + prefix + "field " + field.Name + " : " +
                RenderNullable(new NullabilityInfoContext().Create(field));
            if (field.IsLiteral)
            {
                line += " = " + RenderConstant(field.GetRawConstantValue());
            }

            lines.Add(line);
        }

        foreach (var eventInfo in type.GetEvents(declared))
        {
            var adder = eventInfo.AddMethod;
            if (adder == null || !IsSurface(adder))
            {
                continue;
            }

            lines.Add(
                Accessibility(adder) + " " +
                (adder.IsStatic ? "static " : "") +
                SlotModifier(type, adder) +
                "event " + eventInfo.Name + " : " +
                FriendlyName(eventInfo.EventHandlerType!));
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static string Accessibility(MethodBase method) =>
        method.IsPublic ? "public"
        : method.IsFamilyOrAssembly ? "protected internal"
        : "protected";

    private static string Accessibility(FieldInfo field) =>
        field.IsPublic ? "public"
        : field.IsFamilyOrAssembly ? "protected internal"
        : "protected";

    /// <summary>
    /// The virtual-slot semantics of a member: abstract / virtual / override /
    /// sealed override. A sealed override still has IsVirtual true, so IsFinal and
    /// the NewSlot flag are what actually distinguish the shapes; an implicit
    /// interface implementation (newslot + final) introduces no overridable slot
    /// and renders unmarked, as in C# source.
    /// </summary>
    private static string SlotModifier(Type declaringType, MethodInfo method)
    {
        if (declaringType.IsInterface || !method.IsVirtual)
        {
            return "";
        }

        var newSlot = (method.Attributes & MethodAttributes.NewSlot) != 0;
        if (method.IsAbstract)
        {
            return newSlot ? "abstract " : "abstract override ";
        }

        if (method.IsFinal)
        {
            return newSlot ? "" : "sealed override ";
        }

        return newSlot ? "virtual " : "override ";
    }

    private static string RenderMethodSignature(MethodInfo method)
    {
        var generics = "";
        if (method.IsGenericMethodDefinition)
        {
            var arguments = method.GetGenericArguments();
            generics = "<" + string.Join(", ", arguments.Select(argument => argument.Name)) + ">";
            var constraints = GenericConstraintLines(arguments);
            if (constraints.Length > 0)
            {
                generics += " " + constraints;
            }
        }

        return method.Name + generics + "(" + RenderParameters(method.GetParameters()) + ") : " +
            RenderNullable(new NullabilityInfoContext().Create(method.ReturnParameter));
    }

    private static string RenderParameters(IReadOnlyList<ParameterInfo> parameters)
    {
        var rendered = new string[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            var text = "";
            if (parameter.ParameterType.IsByRef)
            {
                text += parameter.IsOut ? "out "
                    : parameter.IsIn ? "in "
                    : "ref ";
            }

            if (parameter.GetCustomAttributesData().Any(attribute =>
                    attribute.AttributeType == typeof(ParamArrayAttribute)))
            {
                text += "params ";
            }

            text += RenderNullable(new NullabilityInfoContext().Create(parameter));
            text += " " + parameter.Name;
            if (parameter.HasDefaultValue)
            {
                text += " = " + RenderConstant(parameter.RawDefaultValue);
            }

            rendered[i] = text;
        }

        return string.Join(", ", rendered);
    }

    private static string RenderConstant(object? value) =>
        value switch
        {
            null => "null",
            string text => "\"" + text + "\"",
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "?",
        };

    /// <summary>Renders a type with its nullable-reference annotations, recursing into generic arguments and arrays.</summary>
    private static string RenderNullable(NullabilityInfo info)
    {
        var type = info.Type;
        if (type.IsByRef)
        {
            type = type.GetElementType()!;
        }

        string core;
        if (type.IsArray && info.ElementType != null)
        {
            core = RenderNullable(info.ElementType) + "[]";
        }
        else if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            core = FriendlyName(underlying) + "?";
            return core;
        }
        else if (type.IsGenericType && info.GenericTypeArguments.Length > 0)
        {
            var name = type.GetGenericTypeDefinition().FullName ?? type.Name;
            var backtick = name.IndexOf('`');
            if (backtick >= 0)
            {
                name = name.Substring(0, backtick);
            }

            core = name + "<" +
                string.Join(", ", info.GenericTypeArguments.Select(RenderNullable)) + ">";
        }
        else
        {
            core = FriendlyName(type);
        }

        if (!type.IsValueType &&
            (info.ReadState == NullabilityState.Nullable || info.WriteState == NullabilityState.Nullable))
        {
            core += "?";
        }

        return core;
    }

    private static string GenericConstraintLines(IReadOnlyList<Type> arguments)
    {
        var clauses = new List<string>();
        foreach (var argument in arguments)
        {
            if (!argument.IsGenericParameter)
            {
                continue;
            }

            var constraints = new List<string>();
            var attributes = argument.GenericParameterAttributes;
            if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
            {
                constraints.Add("class");
            }

            if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
            {
                constraints.Add("struct");
            }

            foreach (var constraint in argument.GetGenericParameterConstraints()
                         .Where(constraint => constraint != typeof(ValueType))
                         .Select(FriendlyName)
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                constraints.Add(constraint);
            }

            if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0 &&
                (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0)
            {
                constraints.Add("new()");
            }

            var variance = (attributes & GenericParameterAttributes.Covariant) != 0 ? "out "
                : (attributes & GenericParameterAttributes.Contravariant) != 0 ? "in "
                : "";
            if (constraints.Count > 0 || variance.Length > 0)
            {
                clauses.Add("where " + variance + argument.Name +
                    (constraints.Count > 0 ? " : " + string.Join(", ", constraints) : ""));
            }
        }

        return string.Join(" ", clauses);
    }

    private static bool IsSurface(MethodBase method) =>
        method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsSurface(FieldInfo field) =>
        field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    private static string FriendlyName(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray)
        {
            return FriendlyName(type.GetElementType()!) + "[]";
        }

        if (type.IsByRef)
        {
            return FriendlyName(type.GetElementType()!);
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return FriendlyName(underlying) + "?";
        }

        if (type.IsGenericType)
        {
            var name = (type.IsNested
                ? FriendlyName(type.DeclaringType!) + "+" + type.Name
                : type.GetGenericTypeDefinition().FullName ?? type.Name);
            var backtick = name.IndexOf('`');
            if (backtick >= 0)
            {
                name = name.Substring(0, backtick);
            }

            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">";
        }

        if (type.IsNested)
        {
            return FriendlyName(type.DeclaringType!) + "+" + type.Name;
        }

        return type.FullName ?? type.Name;
    }
}
