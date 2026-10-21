using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Linq;

#if UNITY_5_3_OR_NEWER
using UnityEngine;
#endif


internal sealed class ReflectionBroker
{
    private readonly Dictionary<long, string> objectOwners = new Dictionary<long, string>();
    private readonly Dictionary<long, string> typeOwners = new Dictionary<long, string>();
    private string currentOwner;
    private int handleLimit = 65536;

    internal Message HandleOwned(string owner, Message request)
    {
        string previous = currentOwner;
        currentOwner = owner;
        try { return Handle(request); }
        finally { currentOwner = previous; }
    }
    internal void ReleaseOwner(string owner)
    {
        foreach (long id in objectOwners.Where(x => x.Value == owner).Select(x => x.Key).ToArray())
        { objects.Remove(id); objectOwners.Remove(id); }
        foreach (long id in typeOwners.Where(x => x.Value == owner).Select(x => x.Key).ToArray())
        { types.Remove(id); typeOwners.Remove(id); }
    }
    public string Statistics() => "objects=" + objects.Count + " types=" + types.Count + " limit=" + handleLimit;
    public void SetHandleLimit(int maximum)
    {
        if (maximum < objects.Count + types.Count || maximum < 16 || maximum > 1000000)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        handleLimit = maximum;
    }
    public string Describe(object target, string member, int offset, int count)
    {
        if (target == null) return "null";
        if (offset < 0 || count < 1 || count > 64) throw new ArgumentOutOfRangeException(nameof(count));
        Type type = target as Type ?? target.GetType();
        MemberInfo[] members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(x => string.IsNullOrEmpty(member) || x.Name == member)
            .OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.ToString(), StringComparer.Ordinal).ToArray();
        var output = new System.Text.StringBuilder(type.FullName + "\nassembly: " + type.Assembly.GetName().Name +
            "\nmembers: " + members.Length + " (offset " + offset + ")\n");
        foreach (var info in members.Skip(offset).Take(count))
        {
            string line = info.MemberType + " " + info;
            if (output.Length + line.Length > 30000) { output.Append("[output limit]\n"); break; }
            output.AppendLine(line);
        }
        if (offset + count < members.Length) output.AppendLine("More: unity help TARGET \"MEMBER\" OFFSET COUNT");
        return output.ToString();
    }
    private readonly Dictionary<long, object> objects =
        new Dictionary<long, object>();

    private readonly Dictionary<long, Type> types =
        new Dictionary<long, Type>();

    private readonly Dictionary<string, object> roots =
        new Dictionary<string, object>(
            StringComparer.Ordinal
        );

    private long nextObject = 1;
    private long nextType = 1;


    public void RegisterRoot(
        string name,
        object value)
    {
        if (
            string.IsNullOrWhiteSpace(
                name
            )
        )
        {
            throw new ArgumentException(
                "Root name is required.",
                nameof(name)
            );
        }

        if (value == null)
        {
            throw new ArgumentNullException(
                nameof(value)
            );
        }

        roots[name] =
            value;
    }


    /// <summary>Kind, target types and member names of a request, for dispatch
    /// diagnostics only. Reads handles without owner checks or side effects.</summary>
    internal string DescribeRequest(
        Message request)
    {
        var parts = new List<string> { request.Kind.ToString() };
        foreach (Value value in request.Values)
        {
            if (parts.Count >= 4) break;
            if (value.Kind == ValueKind.Object)
                parts.Add(objects.TryGetValue(value.AsObject(), out object target) && target != null
                    ? target.GetType().FullName : "object");
            else if (value.Kind == ValueKind.Type)
                parts.Add(types.TryGetValue(value.AsType(), out Type type) ? type.FullName : "type");
            else if (value.Kind == ValueKind.String)
            {
                string text = value.AsString();
                parts.Add(text.Length > 96 ? text.Substring(0, 96) + "..." : text);
            }
        }
        return string.Join(" ", parts);
    }


    public Message Handle(
        Message request)
    {
        try
        {
            switch (request.Kind)
            {
                case MessageKind.Find:
                    return Find(request);

                case MessageKind.Type:
                    return ResolveType(request);

                case MessageKind.Component:
                    return Component(request);

                case MessageKind.Get:
                    return Get(request);

                case MessageKind.Set:
                    return Set(request);

                case MessageKind.Call:
                    return Call(request);

                case MessageKind.New:
                    return Construct(request);

                case MessageKind.Release:
                    return Release(request);

                case MessageKind.Batch:
                    return Batch(request);

                default:
                    throw new InvalidOperationException(
                        "Unsupported reflection request: " +
                        request.Kind
                    );
            }
        }
        catch (
            Exception error
        )
        {
            return Fault(
                request,
                error.GetType().Name +
                ": " +
                error.Message
            );
        }
    }


    // ========================================================
    // FIND
    // ========================================================

    private Message Find(
        Message request)
    {
        Need(
            request,
            1
        );

        string name =
            request.Values[0]
                .AsString();

        if (
            roots.TryGetValue(
                name,
                out object root
            )
        )
        {
            return Result(
                request,
                Value.Object(
                    Register(
                        root
                    )
                )
            );
        }

#if UNITY_5_3_OR_NEWER

        GameObject gameObject =
            GameObject.Find(
                name
            );

        if (gameObject != null)
        {
            return Result(
                request,
                Value.Object(
                    Register(
                        gameObject
                    )
                )
            );
        }

#endif

        throw new InvalidOperationException(
            "Object not found: " +
            name
        );
    }


    // ========================================================
    // TYPE
    // ========================================================

    private Message ResolveType(
        Message request)
    {
        Need(
            request,
            1
        );

        Type type =
            ResolveTypeName(
                request.Values[0]
                    .AsString()
            );

        long reference =
            RegisterType(
                type
            );

        return Result(
            request,
            Value.TypeRef(
                reference
            )
        );
    }


    private static Type ResolveTypeName(
        string name)
    {
        Type type =
            System.Type.GetType(
                name,
                false
            );

        if (type != null)
            return type;


        // Composed types that Assembly.GetType does not resolve.

        if (
            name.EndsWith(
                "[]",
                StringComparison.Ordinal
            )
        )
        {
            return ResolveTypeName(
                    name.Substring(
                        0,
                        name.Length - 2
                    )
                )
                .MakeArrayType();
        }

        if (
            name.EndsWith(
                "&",
                StringComparison.Ordinal
            )
        )
        {
            return ResolveTypeName(
                    name.Substring(
                        0,
                        name.Length - 1
                    )
                )
                .MakeByRefType();
        }

        if (
            name.EndsWith(
                "*",
                StringComparison.Ordinal
            )
        )
        {
            return ResolveTypeName(
                    name.Substring(
                        0,
                        name.Length - 1
                    )
                )
                .MakePointerType();
        }


        Assembly[] assemblies =
            AppDomain.CurrentDomain
                .GetAssemblies();

        for (
            int i = 0;
            i < assemblies.Length;
            i++
        )
        {
            type =
                assemblies[i].GetType(
                    name,
                    false
                );

            if (type != null)
                return type;
        }

        throw new TypeLoadException(
            "Type not found: " +
            name
        );
    }


    // ========================================================
    // COMPONENT
    // ========================================================

    private Message Component(
        Message request)
    {
        Need(
            request,
            2
        );

#if UNITY_5_3_OR_NEWER

        object target =
            RequireObject(
                request.Values[0]
                    .AsObject()
            );

        Type type =
            RequireType(
                request.Values[1]
                    .AsType()
            );

        GameObject gameObject =
            target as GameObject;

        if (gameObject == null)
        {
            UnityEngine.Component component =
                target as UnityEngine.Component;

            if (component != null)
            {
                gameObject =
                    component.gameObject;
            }
        }

        if (gameObject == null)
        {
            throw new InvalidOperationException(
                "Target is not a GameObject or Component."
            );
        }

        UnityEngine.Component result =
            gameObject.GetComponent(
                type
            );

        if (result == null)
        {
            throw new InvalidOperationException(
                "Component not found: " +
                type.FullName
            );
        }

        return Result(
            request,
            Value.Object(
                Register(
                    result
                )
            )
        );

#else

        throw new PlatformNotSupportedException(
            "Component is Unity-only."
        );

#endif
    }


    // ========================================================
    // GET
    // ========================================================

    private Message Get(
        Message request)
    {
        Need(
            request,
            2
        );

        Value targetValue =
            request.Values[0];

        string name =
            request.Values[1]
                .AsString();


        if (
            targetValue.Kind ==
            ValueKind.Object
        )
        {
            object target =
                RequireObject(
                    targetValue.AsObject()
                );

            return Result(
                request,
                Encode(
                    GetMember(
                        target.GetType(),
                        target,
                        false,
                        name
                    )
                )
            );
        }


        if (
            targetValue.Kind ==
            ValueKind.Type
        )
        {
            Type targetType =
                RequireType(
                    targetValue.AsType()
                );

            if (
                TryGetMember(
                    targetType,
                    null,
                    true,
                    name,
                    out object result
                )
            )
            {
                return Result(
                    request,
                    Encode(
                        result
                    )
                );
            }


            // TypeRef also represents the System.Type object itself
            // when no static member exists on the represented type.

            return Result(
                request,
                Encode(
                    GetMember(
                        typeof(System.Type),
                        targetType,
                        false,
                        name
                    )
                )
            );
        }


        throw new InvalidOperationException(
            "GET target must be an object or type reference."
        );
    }


    private static object GetMember(
        Type type,
        object target,
        bool isStatic,
        string name)
    {
        if (
            TryGetMember(
                type,
                target,
                isStatic,
                name,
                out object value
            )
        )
        {
            return value;
        }

        throw new MissingMemberException(
            type.FullName,
            name
        );
    }


    private static bool TryGetMember(
        Type type,
        object target,
        bool isStatic,
        string name,
        out object value)
    {
        BindingFlags flags =
            BindingFlags.Public |
            (
                isStatic
                    ? BindingFlags.Static |
                      BindingFlags.FlattenHierarchy
                    : BindingFlags.Instance
            );

        PropertyInfo property =
            type.GetProperty(
                name,
                flags
            );

        if (
            property != null &&
            property.GetIndexParameters().Length ==
                0 &&
            property.CanRead
        )
        {
            value =
                property.GetValue(
                    target,
                    null
                );

            return true;
        }

        FieldInfo field =
            type.GetField(
                name,
                flags
            );

        if (field != null)
        {
            value =
                field.GetValue(
                    target
                );

            return true;
        }

        value = null;

        return false;
    }


    // ========================================================
    // SET
    // ========================================================

    private Message Set(
        Message request)
    {
        Need(
            request,
            3
        );

        Value targetValue =
            request.Values[0];

        string name =
            request.Values[1]
                .AsString();

        Value value =
            request.Values[2];


        if (
            targetValue.Kind ==
            ValueKind.Object
        )
        {
            object target =
                RequireObject(
                    targetValue.AsObject()
                );

            SetMember(
                target.GetType(),
                target,
                false,
                name,
                value
            );

            return Result(
                request
            );
        }


        if (
            targetValue.Kind ==
            ValueKind.Type
        )
        {
            Type targetType =
                RequireType(
                    targetValue.AsType()
                );

            if (
                TrySetMember(
                    targetType,
                    null,
                    true,
                    name,
                    value
                )
            )
            {
                return Result(
                    request
                );
            }

            SetMember(
                typeof(System.Type),
                targetType,
                false,
                name,
                value
            );

            return Result(
                request
            );
        }


        throw new InvalidOperationException(
            "SET target must be an object or type reference."
        );
    }


    private void SetMember(
        Type type,
        object target,
        bool isStatic,
        string name,
        Value value)
    {
        if (
            TrySetMember(
                type,
                target,
                isStatic,
                name,
                value
            )
        )
            return;

        throw new MissingMemberException(
            type.FullName,
            name
        );
    }


    private bool TrySetMember(
        Type type,
        object target,
        bool isStatic,
        string name,
        Value value)
    {
        BindingFlags flags =
            BindingFlags.Public |
            (
                isStatic
                    ? BindingFlags.Static |
                      BindingFlags.FlattenHierarchy
                    : BindingFlags.Instance
            );

        PropertyInfo property =
            type.GetProperty(
                name,
                flags
            );

        if (
            property != null &&
            property.GetIndexParameters().Length ==
                0
        )
        {
            if (!property.CanWrite)
            {
                throw new InvalidOperationException(
                    "Property is readonly: " +
                    name
                );
            }

            property.SetValue(
                target,
                Decode(
                    value,
                    property.PropertyType
                ),
                null
            );

            return true;
        }

        FieldInfo field =
            type.GetField(
                name,
                flags
            );

        if (field != null)
        {
            if (field.IsInitOnly)
            {
                throw new InvalidOperationException(
                    "Field is readonly: " +
                    name
                );
            }

            field.SetValue(
                target,
                Decode(
                    value,
                    field.FieldType
                )
            );

            return true;
        }

        return false;
    }


    // ========================================================
    // CALL
    //
    // Payload:
    //
    // target
    // method name
    // generic count
    // generic type refs...
    // signature count (-1 = infer)
    // signature type refs...
    // arguments...
    // ========================================================

    private Message Call(
        Message request)
    {
        CallSpec spec =
            ParseCall(
                request
            );


        // ObjectRef = ordinary instance call.

        if (
            spec.Target.Kind ==
            ValueKind.Object
        )
        {
            object target =
                RequireObject(
                    spec.Target.AsObject()
                );

            return Invoke(
                request,
                target.GetType(),
                target,
                false,
                spec
            );
        }


        // TypeRef first means a static member on that type.

        if (
            spec.Target.Kind ==
            ValueKind.Type
        )
        {
            Type represented =
                RequireType(
                    spec.Target.AsType()
                );

            if (
                TryResolveMethod(
                    represented,
                    null,
                    true,
                    spec,
                    out MethodInfo method,
                    out object[] decoded
                )
            )
            {
                return InvokeResolved(
                    request,
                    method,
                    null,
                    decoded
                );
            }


            // If there is no static match, treat the TypeRef
            // as the actual System.Type instance. This makes:
            //
            //   MakeArrayType
            //   MakeByRefType
            //   MakeGenericType
            //
            // reachable without another protocol concept.

            if (
                TryResolveMethod(
                    typeof(System.Type),
                    represented,
                    false,
                    spec,
                    out method,
                    out decoded
                )
            )
            {
                return InvokeResolved(
                    request,
                    method,
                    represented,
                    decoded
                );
            }

            throw new MissingMethodException(
                represented.FullName,
                spec.Name
            );
        }


        throw new InvalidOperationException(
            "CALL target must be an object or type reference."
        );
    }


    private Message Invoke(
        Message request,
        Type type,
        object target,
        bool isStatic,
        CallSpec spec)
    {
        if (
            !TryResolveMethod(
                type,
                target,
                isStatic,
                spec,
                out MethodInfo method,
                out object[] decoded
            )
        )
        {
            throw new MissingMethodException(
                type.FullName,
                spec.Name
            );
        }

        return InvokeResolved(
            request,
            method,
            target,
            decoded
        );
    }


    private bool TryResolveMethod(
        Type type,
        object target,
        bool isStatic,
        CallSpec spec,
        out MethodInfo method,
        out object[] decoded)
    {
        method = null;
        decoded = null;

        BindingFlags flags =
            BindingFlags.Public |
            (
                isStatic
                    ? BindingFlags.Static |
                      BindingFlags.FlattenHierarchy
                    : BindingFlags.Instance
            );

        MethodInfo[] candidates =
            type.GetMethods(
                flags
            );

        for (
            int i = 0;
            i < candidates.Length;
            i++
        )
        {
            MethodInfo candidate =
                candidates[i];

            if (
                candidate.Name !=
                spec.Name
            )
                continue;


            MethodInfo usable =
                candidate;


            if (
                spec.GenericTypes.Length >
                0
            )
            {
                if (
                    !candidate
                        .IsGenericMethodDefinition
                )
                    continue;

                if (
                    candidate
                        .GetGenericArguments()
                        .Length !=
                    spec.GenericTypes.Length
                )
                    continue;

                try
                {
                    usable =
                        candidate
                            .MakeGenericMethod(
                                spec.GenericTypes
                            );
                }
                catch
                {
                    continue;
                }
            }
            else
            {
                if (
                    candidate
                        .ContainsGenericParameters
                )
                    continue;
            }


            ParameterInfo[] parameters =
                usable.GetParameters();

            if (
                parameters.Length !=
                spec.Arguments.Length
            )
                continue;


            if (
                spec.Signature !=
                null
            )
            {
                bool signatureMatches =
                    true;

                for (
                    int n = 0;
                    n < parameters.Length;
                    n++
                )
                {
                    if (
                        parameters[n]
                            .ParameterType !=
                        spec.Signature[n]
                    )
                    {
                        signatureMatches =
                            false;

                        break;
                    }
                }

                if (!signatureMatches)
                    continue;
            }


            if (
                !TryDecodeArguments(
                    spec.Arguments,
                    parameters,
                    out object[] values
                )
            )
                continue;


            if (method != null)
            {
                throw new AmbiguousMatchException(
                    "Ambiguous call: " +
                    spec.Name
                );
            }

            method =
                usable;

            decoded =
                values;
        }

        return method != null;
    }


    private Message InvokeResolved(
        Message request,
        MethodInfo method,
        object target,
        object[] decoded)
    {
        object result;

        try
        {
            result =
                method.Invoke(
                    target,
                    decoded
                );
        }
        catch (
            TargetInvocationException error
        )
        {
            throw error.InnerException ??
                error;
        }


        ParameterInfo[] parameters =
            method.GetParameters();

        var results =
            new List<Value>();


        // Result[0] is always the primary return value.
        // Void is represented as null.

        if (
            method.ReturnType ==
            typeof(void)
        )
        {
            results.Add(
                Value.Null()
            );
        }
        else
        {
            results.Add(
                Encode(
                    result
                )
            );
        }


        // ref/out values come back as:
        //
        // index, value, index, value...

        for (
            int i = 0;
            i < parameters.Length;
            i++
        )
        {
            if (
                parameters[i]
                    .ParameterType
                    .IsByRef ||
                parameters[i]
                    .IsOut
            )
            {
                results.Add(
                    Value.Int32(i)
                );

                results.Add(
                    Encode(
                        decoded[i]
                    )
                );
            }
        }

        return Result(
            request,
            results.ToArray()
        );
    }


    private CallSpec ParseCall(
        Message request)
    {
        if (
            request.Values.Length <
            4
        )
        {
            throw new InvalidOperationException(
                "Invalid CALL payload."
            );
        }

        int index =
            0;

        Value target =
            request.Values[index++];

        string name =
            request.Values[index++]
                .AsString();


        int genericCount =
            request.Values[index++]
                .AsInt32();

        if (
            genericCount < 0 ||
            index + genericCount >
                request.Values.Length
        )
        {
            throw new InvalidOperationException(
                "Invalid generic argument count."
            );
        }

        var genericTypes =
            new Type[genericCount];

        for (
            int i = 0;
            i < genericCount;
            i++
        )
        {
            genericTypes[i] =
                RequireType(
                    request.Values[index++]
                        .AsType()
                );
        }


        if (
            index >=
            request.Values.Length
        )
        {
            throw new InvalidOperationException(
                "Missing signature count."
            );
        }

        int signatureCount =
            request.Values[index++]
                .AsInt32();

        Type[] signature =
            null;

        if (
            signatureCount >= 0
        )
        {
            if (
                index + signatureCount >
                request.Values.Length
            )
            {
                throw new InvalidOperationException(
                    "Invalid signature count."
                );
            }

            signature =
                new Type[
                    signatureCount
                ];

            for (
                int i = 0;
                i < signatureCount;
                i++
            )
            {
                signature[i] =
                    RequireType(
                        request.Values[index++]
                            .AsType()
                    );
            }
        }


        Value[] arguments =
            Tail(
                request.Values,
                index
            );

        if (
            signature != null &&
            signature.Length !=
                arguments.Length
        )
        {
            throw new InvalidOperationException(
                "Exact signature and argument counts differ."
            );
        }

        return new CallSpec(
            target,
            name,
            genericTypes,
            signature,
            arguments
        );
    }


    private sealed class CallSpec
    {
        public readonly Value Target;
        public readonly string Name;
        public readonly Type[] GenericTypes;
        public readonly Type[] Signature;
        public readonly Value[] Arguments;


        public CallSpec(
            Value target,
            string name,
            Type[] genericTypes,
            Type[] signature,
            Value[] arguments)
        {
            Target = target;
            Name = name;
            GenericTypes = genericTypes;
            Signature = signature;
            Arguments = arguments;
        }
    }


    // ========================================================
    // NEW
    //
    // Payload:
    //
    // type ref
    // signature count (-1 = infer)
    // signature type refs...
    // arguments...
    // ========================================================

    private Message Construct(
        Message request)
    {
        if (
            request.Values.Length <
            2
        )
        {
            throw new InvalidOperationException(
                "Invalid NEW payload."
            );
        }

        int index =
            0;

        Type type =
            RequireType(
                request.Values[index++]
                    .AsType()
            );

        int signatureCount =
            request.Values[index++]
                .AsInt32();

        Type[] signature =
            null;

        if (
            signatureCount >= 0
        )
        {
            if (
                index + signatureCount >
                request.Values.Length
            )
            {
                throw new InvalidOperationException(
                    "Invalid constructor signature."
                );
            }

            signature =
                new Type[
                    signatureCount
                ];

            for (
                int i = 0;
                i < signatureCount;
                i++
            )
            {
                signature[i] =
                    RequireType(
                        request.Values[index++]
                            .AsType()
                    );
            }
        }

        Value[] arguments =
            Tail(
                request.Values,
                index
            );

        if (
            signature != null &&
            signature.Length !=
                arguments.Length
        )
        {
            throw new InvalidOperationException(
                "Constructor signature and argument counts differ."
            );
        }


        ConstructorInfo match =
            null;

        object[] decoded =
            null;

        ConstructorInfo[] constructors =
            type.GetConstructors(
                BindingFlags.Public |
                BindingFlags.Instance
            );

        for (
            int i = 0;
            i < constructors.Length;
            i++
        )
        {
            ConstructorInfo candidate =
                constructors[i];

            ParameterInfo[] parameters =
                candidate.GetParameters();

            if (
                parameters.Length !=
                arguments.Length
            )
                continue;


            if (
                signature !=
                null
            )
            {
                bool signatureMatches =
                    true;

                for (
                    int n = 0;
                    n < parameters.Length;
                    n++
                )
                {
                    if (
                        parameters[n]
                            .ParameterType !=
                        signature[n]
                    )
                    {
                        signatureMatches =
                            false;

                        break;
                    }
                }

                if (!signatureMatches)
                    continue;
            }


            if (
                !TryDecodeArguments(
                    arguments,
                    parameters,
                    out object[] values
                )
            )
                continue;


            if (match != null)
            {
                throw new AmbiguousMatchException(
                    "Ambiguous constructor: " +
                    type.FullName
                );
            }

            match =
                candidate;

            decoded =
                values;
        }

        if (match == null)
        {
            throw new MissingMethodException(
                "Constructor not found: " +
                type.FullName
            );
        }


        object result;

        try
        {
            result =
                match.Invoke(
                    decoded
                );
        }
        catch (
            TargetInvocationException error
        )
        {
            throw error.InnerException ??
                error;
        }


        var results =
            new List<Value>();

        results.Add(
            Encode(
                result
            )
        );


        ParameterInfo[] usedParameters =
            match.GetParameters();

        for (
            int i = 0;
            i < usedParameters.Length;
            i++
        )
        {
            if (
                usedParameters[i]
                    .ParameterType
                    .IsByRef ||
                usedParameters[i]
                    .IsOut
            )
            {
                results.Add(
                    Value.Int32(i)
                );

                results.Add(
                    Encode(
                        decoded[i]
                    )
                );
            }
        }


        return Result(
            request,
            results.ToArray()
        );
    }


    // ========================================================
    // RELEASE
    // ========================================================

    private Message Release(
        Message request)
    {
        Need(
            request,
            1
        );

        Value reference =
            request.Values[0];

        if (
            reference.Kind ==
            ValueKind.Object
        )
        {
            RequireOwner(objectOwners, reference.AsObject());
            objectOwners.Remove(reference.AsObject());
            objects.Remove(
                reference.AsObject()
            );
        }
        else if (
            reference.Kind ==
            ValueKind.Type
        )
        {
            RequireOwner(typeOwners, reference.AsType());
            typeOwners.Remove(reference.AsType());
            types.Remove(
                reference.AsType()
            );
        }
        else
        {
            throw new InvalidOperationException(
                "RELEASE requires an object or type reference."
            );
        }

        return Result(
            request
        );
    }


    // ========================================================
    // BATCH
    //
    // Each value is Bytes(Wire.Pack(submessage)).
    //
    // Value.BatchRef(N) inside a submessage is replaced by
    // the primary result of operation N.
    //
    // Batches are sequential and intentionally NOT transactional.
    // ========================================================

    private Message Batch(
        Message request)
    {
        var primary =
            new List<Value>();

        for (
            int i = 0;
            i < request.Values.Length;
            i++
        )
        {
            byte[] packed =
                request.Values[i]
                    .AsBytes();

            Message operation =
                Wire.Unpack(
                    packed
                );

            if (
                operation.Kind ==
                    MessageKind.Batch ||
                operation.Kind ==
                    MessageKind.Hello ||
                operation.Kind ==
                    MessageKind.Ready ||
                operation.Kind ==
                    MessageKind.Result ||
                operation.Kind ==
                    MessageKind.Fault
            )
            {
                return Fault(
                    request,
                    "Invalid operation in batch at index " +
                    i +
                    "."
                );
            }


            Value[] values =
                new Value[
                    operation.Values.Length
                ];

            for (
                int n = 0;
                n < values.Length;
                n++
            )
            {
                Value value =
                    operation.Values[n];

                if (
                    value.Kind ==
                    ValueKind.Batch
                )
                {
                    int source =
                        value.AsBatch();

                    if (
                        source < 0 ||
                        source >=
                            primary.Count
                    )
                    {
                        return Fault(
                            request,
                            "Invalid batch reference $" +
                            source +
                            " at operation " +
                            i +
                            "."
                        );
                    }

                    value =
                        primary[source];
                }

                values[n] =
                    value;
            }


            Message resolved =
                new Message(
                    operation.Id,
                    operation.Kind,
                    values
                );

            Message response =
                Handle(
                    resolved
                );


            if (
                response.Kind ==
                MessageKind.Fault
            )
            {
                string reason =
                    response.Values.Length >
                        0 &&
                    response.Values[0].Kind ==
                        ValueKind.String
                        ? response.Values[0]
                            .AsString()
                        : "Unknown batch failure.";

                return Fault(
                    request,
                    "Batch[" +
                    i +
                    "]: " +
                    reason
                );
            }


            Value result =
                response.Values.Length >
                    0
                    ? response.Values[0]
                    : Value.Null();

            primary.Add(
                result
            );
        }


        return Result(
            request,
            primary.ToArray()
        );
    }


    // ========================================================
    // HANDLES / VALUES
    // ========================================================

    private long Register(
        object value)
    {
        CheckHandleBudget();
        long reference =
            nextObject++;

        objects.Add(
            reference,
            value
        );
        objectOwners[reference] = currentOwner;

        return reference;
    }


    private long RegisterType(
        Type value)
    {
        CheckHandleBudget();
        long reference =
            nextType++;

        types.Add(
            reference,
            value
        );
        typeOwners[reference] = currentOwner;

        return reference;
    }


    private object RequireObject(
        long reference)
    {
        RequireOwner(objectOwners, reference);
        if (
            !objects.TryGetValue(
                reference,
                out object value
            )
        )
        {
            throw new KeyNotFoundException(
                "Unknown object reference: " +
                reference
            );
        }

        return value;
    }


    private Type RequireType(
        long reference)
    {
        RequireOwner(typeOwners, reference);
        if (
            !types.TryGetValue(
                reference,
                out Type value
            )
        )
        {
            throw new KeyNotFoundException(
                "Unknown type reference: " +
                reference
            );
        }

        return value;
    }


    private void CheckHandleBudget()
    {
        if (objects.Count + types.Count >= handleLimit)
            throw new InvalidOperationException("Reflection handle limit reached; release unused handles.");
    }
    private void RequireOwner(Dictionary<long, string> owners, long reference)
    {
        if (currentOwner != null && owners.TryGetValue(reference, out string owner) && owner != currentOwner)
            throw new InvalidOperationException("Reflection handle belongs to a different IPC connection.");
    }
    private Value Encode(
        object value)
    {
        if (value == null)
            return Value.Null();


#if UNITY_5_3_OR_NEWER

        UnityEngine.Object unity =
            value as UnityEngine.Object;

        if (
            (object)unity != null &&
            unity == null
        )
        {
            return Value.Null();
        }

#endif


        if (value is bool)
            return Value.Boolean(
                (bool)value
            );

        if (value is int)
            return Value.Int32(
                (int)value
            );

        // Widen small integral scalars losslessly instead of registering them
        // as opaque objects. The wire's numeric widths are Int32 and Int64.
        if (value is byte || value is sbyte || value is short || value is ushort)
            return Value.Int32(Convert.ToInt32(value));
        if (value is uint)
            return Value.Int64((uint)value);

        if (value is long)
            return Value.Int64(
                (long)value
            );

        if (value is float)
            return Value.Single(
                (float)value
            );

        if (value is double)
            return Value.Double(
                (double)value
            );

        if (value is string)
            return Value.String(
                (string)value
            );

        if (value is System.Type)
        {
            return Value.TypeRef(
                RegisterType(
                    (System.Type)value
                )
            );
        }

        if (
            value.GetType()
                .IsEnum
        )
        {
            return Value.String(
                value.ToString()
            );
        }

        // Mutable CLR values, including every array size, keep their host
        // identity. Inline byte payloads are an explicit input representation;
        // returned byte[] values follow the same retained path as other arrays.
        return Value.Object(
            Register(
                value
            )
        );
    }


    internal object DecodeValue(Value value, Type target) => Decode(value, target);

    private object Decode(
        Value value,
        Type target)
    {
        if (
            value.Kind ==
            ValueKind.Null
        )
        {
            if (
                target.IsValueType &&
                Nullable.GetUnderlyingType(
                    target
                ) == null
            )
            {
                throw new InvalidCastException(
                    "Cannot assign null to " +
                    target.FullName
                );
            }

            return null;
        }


        Type nullable =
            Nullable.GetUnderlyingType(
                target
            );

        if (nullable != null)
        {
            object inner =
                Decode(
                    value,
                    nullable
                );

            return Activator.CreateInstance(
                target,
                inner
            );
        }


        if (
            value.Kind ==
            ValueKind.Object
        )
        {
            object referenced =
                RequireObject(
                    value.AsObject()
                );

            if (
                !target.IsInstanceOfType(
                    referenced
                )
            )
            {
                throw new InvalidCastException(
                    referenced.GetType().FullName +
                    " cannot be assigned to " +
                    target.FullName
                );
            }

            return referenced;
        }


        if (
            value.Kind ==
            ValueKind.Type
        )
        {
            Type referenced =
                RequireType(
                    value.AsType()
                );

            if (
                target !=
                typeof(System.Type) &&
                target !=
                typeof(object)
            )
            {
                throw new InvalidCastException(
                    "Type reference cannot be assigned to " +
                    target.FullName
                );
            }

            return referenced;
        }


        if (
            value.Kind ==
                ValueKind.Bytes &&
            target ==
                typeof(byte[])
        )
        {
            return value.AsBytes();
        }


        if (
            target ==
            typeof(object)
        )
        {
            return value.Raw;
        }


        if (target.IsEnum)
        {
            if (
                value.Kind ==
                ValueKind.String
            )
            {
                return Enum.Parse(
                    target,
                    value.AsString(),
                    true
                );
            }

            return Enum.ToObject(
                target,
                Convert.ToInt64(
                    value.Raw,
                    CultureInfo.InvariantCulture
                )
            );
        }


        if (
            target ==
            typeof(string)
        )
        {
            return Convert.ToString(
                value.Raw,
                CultureInfo.InvariantCulture
            );
        }


        if (
            target.IsInstanceOfType(
                value.Raw
            )
        )
        {
            return value.Raw;
        }


        return Convert.ChangeType(
            value.Raw,
            target,
            CultureInfo.InvariantCulture
        );
    }


    private bool TryDecodeArguments(
        Value[] values,
        ParameterInfo[] parameters,
        out object[] decoded)
    {
        decoded =
            new object[
                values.Length
            ];

        try
        {
            for (
                int i = 0;
                i < values.Length;
                i++
            )
            {
                Type parameterType =
                    parameters[i]
                        .ParameterType;

                if (
                    parameterType.IsByRef
                )
                {
                    parameterType =
                        parameterType
                            .GetElementType();
                }


                if (
                    parameters[i]
                        .IsOut
                )
                {
                    decoded[i] =
                        parameterType.IsValueType
                            ? Activator.CreateInstance(
                                parameterType
                            )
                            : null;

                    continue;
                }


                decoded[i] =
                    Decode(
                        values[i],
                        parameterType
                    );
            }

            return true;
        }
        catch
        {
            decoded =
                null;

            return false;
        }
    }


    // ========================================================
    // HELPERS
    // ========================================================

    private static Value[] Tail(
        Value[] source,
        int start)
    {
        var result =
            new Value[
                source.Length -
                start
            ];

        Array.Copy(
            source,
            start,
            result,
            0,
            result.Length
        );

        return result;
    }


    private static void Need(
        Message message,
        int count)
    {
        if (
            message.Values.Length !=
            count
        )
        {
            throw new InvalidOperationException(
                "Invalid reflection argument count."
            );
        }
    }


    private static Message Result(
        Message request,
        params Value[] values)
    {
        return new Message(
            request.Id,
            MessageKind.Result,
            values
        );
    }


    private static Message Fault(
        Message request,
        string reason)
    {
        return new Message(
            request.Id,
            MessageKind.Fault,
            Value.String(
                reason
            )
        );
    }
}
