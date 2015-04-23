using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using NServiceBus.Features;
using NServiceBus.Logging;
using NServiceBus.ObjectBuilder;
using NServiceBus.Serialization;
using NServiceBus.Serializers.Json;

namespace NServiceBus.Workarounds
{
    using System.Linq;
    using NServiceBus.MessageInterfaces;
    using NServiceBus.Config;

    /// <summary>
    /// Defines the capabilities of the JSON serializer
    /// </summary>
    public class JsonSerializer : SerializationDefinition
    {
        /// <summary>
        /// <see cref="SerializationDefinition.ProvidedByFeature"/>
        /// </summary>
        protected override Type ProvidedByFeature()
        {
            return typeof(JsonSerialization);
        }
    }

    public class JsonSerialization : Feature
    {
        internal JsonSerialization()
        {
            EnableByDefault();
            Prerequisite(this.ShouldSerializationFeatureBeEnabled, "JsonSerialization not enable since serialization definition not detected.");
        }


        /// <summary>
        /// See <see cref="Feature.Setup"/>
        /// </summary>
        protected override void Setup(FeatureConfigurationContext context)
        {
            context.Container.ConfigureComponent<MessageMapper>(DependencyLifecycle.SingleInstance);
            var c = context.Container.ConfigureComponent<JsonMessageSerializer>(DependencyLifecycle.SingleInstance);

            context.Settings.ApplyTo<JsonMessageSerializer>((IComponentConfig)c);
        }
    }

    /// <summary>
    /// Initializes the mapper and the serializer with the found message types
    /// </summary>
    class MessageTypesInitializer : IWantToRunWhenConfigurationIsComplete
    {
        public MessageMapper Mapper { get; set; }

        public void Run(Configure config)
        {
            if (Mapper == null)
            {
                return;
            }

            var messageTypes = config.TypesToScan.Where(config.Settings.Get<Conventions>().IsMessageType).ToList();

            Mapper.Initialize(messageTypes);
        }
    }

    /// <summary>
    /// Uses reflection to map between interfaces and their generated concrete implementations.
    /// </summary>
    public class MessageMapper : IMessageMapper
    {

        ConcreteProxyCreator concreteProxyCreator;

        /// <summary>
        /// Initializes a new instance of <see cref="MessageMapper"/>.
        /// </summary>
        public MessageMapper()
        {
            concreteProxyCreator = new ConcreteProxyCreator();
        }

        /// <summary>
        /// Scans the given types generating concrete classes for interfaces.
        /// </summary>
        public void Initialize(IEnumerable<Type> types)
        {
            if (types == null)
            {
                return;
            }

            foreach (var t in types)
            {
                InitType(t);
            }
        }

        /// <summary>
        /// Generates a concrete implementation of the given type if it is an interface.
        /// </summary>
        void InitType(Type t)
        {
            if (t == null)
            {
                return;
            }

            if (t.IsSimpleType() || t.IsGenericTypeDefinition)
            {
                return;
            }

            if (typeof(IEnumerable).IsAssignableFrom(t))
            {
                InitType(t.GetElementType());

                foreach (var interfaceType in t.GetInterfaces())
                {
                    foreach (var g in interfaceType.GetGenericArguments())
                    {
                        if (g == t)
                            continue;

                        InitType(g);
                    }
                }

                return;
            }

            var typeName = GetTypeName(t);

            //already handled this type, prevent infinite recursion
            if (nameToType.ContainsKey(typeName))
            {
                return;
            }

            if (t.IsInterface)
            {
                GenerateImplementationFor(t);
            }
            else
            {
                typeToConstructor[t] = t.GetConstructor(Type.EmptyTypes);
            }

            nameToType[typeName] = t;

            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                InitType(field.FieldType);
            }

            foreach (var prop in t.GetProperties())
            {
                InitType(prop.PropertyType);
            }
        }

        void GenerateImplementationFor(Type interfaceType)
        {
            if (!interfaceType.IsVisible)
            {
                throw new Exception(string.Format("We can only generate a concrete implementation for '{0}' if '{0}' is public.", interfaceType));
            }

            if (interfaceType.GetMethods().Any(mi => !(mi.IsSpecialName && (mi.Name.StartsWith("set_") || mi.Name.StartsWith("get_")))))
            {
                Logger.Warn(string.Format("Interface {0} contains methods and can there for not be mapped. Be aware that non mapped interface can't be used to send messages.", interfaceType.Name));
                return;
            }

            var mapped = concreteProxyCreator.CreateTypeFrom(interfaceType);
            interfaceToConcreteTypeMapping[interfaceType] = mapped;
            concreteToInterfaceTypeMapping[mapped] = interfaceType;
            typeToConstructor[mapped] = mapped.GetConstructor(Type.EmptyTypes);
        }

        static string GetTypeName(Type t)
        {
            var args = t.GetGenericArguments();
            if (args.Length == 2)
            {
                if (typeof(KeyValuePair<,>).MakeGenericType(args[0], args[1]) == t)
                {
                    return t.SerializationFriendlyName();
                }
            }

            return t.FullName;
        }

        /// <summary>
        /// If the given type is concrete, returns the interface it was generated to support.
        /// If the given type is an interface, returns the concrete class generated to implement it.
        /// </summary>
        public Type GetMappedTypeFor(Type t)
        {
            if (t.IsClass)
            {
                Type result;
                concreteToInterfaceTypeMapping.TryGetValue(t, out result);
                if (result != null || t.IsGenericTypeDefinition)
                {
                    return result;
                }

                return t;
            }

            Type toReturn;
            interfaceToConcreteTypeMapping.TryGetValue(t, out toReturn);
            return toReturn;
        }

        /// <summary>
        /// Returns the type mapped to the given name.
        /// </summary>
        public Type GetMappedTypeFor(string typeName)
        {
            var name = typeName;
            if (typeName.EndsWith(ConcreteProxyCreator.SUFFIX, StringComparison.Ordinal))
            {
                name = typeName.Substring(0, typeName.Length - ConcreteProxyCreator.SUFFIX.Length);
            }

            Type type;
            if (nameToType.TryGetValue(name, out type))
            {
                return type;
            }

            return Type.GetType(name);
        }

        /// <summary>
        /// Calls the generic CreateInstance and performs the given action on the result.
        /// </summary>
        public T CreateInstance<T>(Action<T> action)
        {
            var result = CreateInstance<T>();

            if (action != null)
            {
                action(result);
            }

            return result;
        }

        /// <summary>
        /// Calls the <see cref="CreateInstance(Type)"/> and returns its result cast to <typeparamref name="T"/>.
        /// </summary>
        public T CreateInstance<T>()
        {
            return (T)CreateInstance(typeof(T));
        }

        /// <summary>
        /// If the given type is an interface, finds its generated concrete implementation, instantiates it, and returns the result.
        /// </summary>
        public object CreateInstance(Type t)
        {
            var mapped = t;
            if (t.IsInterface || t.IsAbstract)
            {
                mapped = GetMappedTypeFor(t);
                if (mapped == null)
                {
                    throw new ArgumentException("Could not find a concrete type mapped to " + t.FullName);
                }
            }

            ConstructorInfo constructor;
            typeToConstructor.TryGetValue(mapped, out constructor);
            if (constructor != null)
            {
                return constructor.Invoke(null);
            }

            return FormatterServices.GetUninitializedObject(mapped);
        }

        Dictionary<Type, Type> interfaceToConcreteTypeMapping = new Dictionary<Type, Type>();
        Dictionary<Type, Type> concreteToInterfaceTypeMapping = new Dictionary<Type, Type>();
        Dictionary<string, Type> nameToType = new Dictionary<string, Type>();
        Dictionary<Type, ConstructorInfo> typeToConstructor = new Dictionary<Type, ConstructorInfo>();
        static ILog Logger = LogManager.GetLogger<MessageMapper>();
    }

    class ConcreteProxyCreator
    {
        internal const string SUFFIX = "__impl";
        ModuleBuilder moduleBuilder;

        public ConcreteProxyCreator()
        {
            var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName("NServiceBusMessageProxies"),
                AssemblyBuilderAccess.Run
                );

            moduleBuilder = assemblyBuilder.DefineDynamicModule("NServiceBusMessageProxies");
        }

        /// <summary>
        /// Generates the concrete implementation of the given type.
        /// Only properties on the given type are generated in the concrete implementation.
        /// </summary>
        public Type CreateTypeFrom(Type type)
        {
            var typeBuilder = moduleBuilder.DefineType(type.FullName + SUFFIX,
                TypeAttributes.Serializable | TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed,
                typeof(object)
                );

            typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

            foreach (var prop in GetAllProperties(type))
            {
                var propertyType = prop.PropertyType;

                var fieldBuilder = typeBuilder.DefineField(
                    "field_" + prop.Name,
                    propertyType,
                    FieldAttributes.Private);

                var propBuilder = typeBuilder.DefineProperty(
                    prop.Name,
                    prop.Attributes | PropertyAttributes.HasDefault,
                    propertyType,
                    null);

                foreach (var customAttribute in prop.GetCustomAttributes(true))
                {
                    AddCustomAttributeToProperty(customAttribute, propBuilder);
                }

                var getMethodBuilder = typeBuilder.DefineMethod(
                    "get_" + prop.Name,
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.VtableLayoutMask,
                    propertyType,
                    Type.EmptyTypes);

                var getIL = getMethodBuilder.GetILGenerator();
                // For an instance property, argument zero is the instance. Load the 
                // instance, then load the private field and return, leaving the
                // field value on the stack.
                getIL.Emit(OpCodes.Ldarg_0);
                getIL.Emit(OpCodes.Ldfld, fieldBuilder);
                getIL.Emit(OpCodes.Ret);

                // Define the "set" accessor method for Number, which has no return
                // type and takes one argument of type int (Int32).
                var setMethodBuilder = typeBuilder.DefineMethod(
                    "set_" + prop.Name,
                    getMethodBuilder.Attributes,
                    null,
                    new[] { propertyType });

                var setIL = setMethodBuilder.GetILGenerator();
                // Load the instance and then the numeric argument, then store the
                // argument in the field.
                setIL.Emit(OpCodes.Ldarg_0);
                setIL.Emit(OpCodes.Ldarg_1);
                setIL.Emit(OpCodes.Stfld, fieldBuilder);
                setIL.Emit(OpCodes.Ret);

                // Last, map the "get" and "set" accessor methods to the 
                // PropertyBuilder. The property is now complete. 
                propBuilder.SetGetMethod(getMethodBuilder);
                propBuilder.SetSetMethod(setMethodBuilder);
            }

            typeBuilder.AddInterfaceImplementation(type);

            return typeBuilder.CreateType();
        }

        /// <summary>
        /// Given a custom attribute and property builder, adds an instance of custom attribute
        /// to the property builder
        /// </summary>
        void AddCustomAttributeToProperty(object customAttribute, PropertyBuilder propBuilder)
        {
            var customAttributeBuilder = BuildCustomAttribute(customAttribute);
            if (customAttributeBuilder != null)
            {
                propBuilder.SetCustomAttribute(customAttributeBuilder);
            }
        }

        static CustomAttributeBuilder BuildCustomAttribute(object customAttribute)
        {
            ConstructorInfo longestCtor = null;
            // Get constructor with the largest number of parameters
            foreach (var cInfo in customAttribute.GetType().GetConstructors().
                Where(cInfo => longestCtor == null || longestCtor.GetParameters().Length < cInfo.GetParameters().Length))
                longestCtor = cInfo;

            if (longestCtor == null)
            {
                return null;
            }

            // For each constructor parameter, get corresponding (by name similarity) property and get its value
            var args = new object[longestCtor.GetParameters().Length];
            var position = 0;
            foreach (var consParamInfo in longestCtor.GetParameters())
            {
                var attrPropInfo = customAttribute.GetType().GetProperty(consParamInfo.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (attrPropInfo != null)
                {
                    args[position] = attrPropInfo.GetValue(customAttribute, null);
                }
                else
                {
                    args[position] = null;
                    var attrFieldInfo = customAttribute.GetType().GetField(consParamInfo.Name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (attrFieldInfo == null)
                    {
                        if (consParamInfo.ParameterType.IsValueType)
                        {
                            args[position] = Activator.CreateInstance(consParamInfo.ParameterType);
                        }
                    }
                    else
                    {
                        args[position] = attrFieldInfo.GetValue(customAttribute);
                    }
                }
                ++position;
            }

            var propList = new List<PropertyInfo>();
            var propValueList = new List<object>();
            foreach (var attrPropInfo in customAttribute.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!attrPropInfo.CanWrite)
                {
                    continue;
                }
                object defaultValue = null;
                var defaultAttributes = attrPropInfo.GetCustomAttributes(typeof(DefaultValueAttribute), true);
                if (defaultAttributes.Length > 0)
                {
                    defaultValue = ((DefaultValueAttribute)defaultAttributes[0]).Value;
                }
                var value = attrPropInfo.GetValue(customAttribute, null);
                if (value == defaultValue)
                {
                    continue;
                }
                propList.Add(attrPropInfo);
                propValueList.Add(value);
            }
            return new CustomAttributeBuilder(longestCtor, args, propList.ToArray(), propValueList.ToArray());
        }

        /// <summary>
        /// Returns all properties on the given type, going up the inheritance hierarchy.
        /// </summary>
        static IEnumerable<PropertyInfo> GetAllProperties(Type type)
        {
            var props = new List<PropertyInfo>(type.GetProperties());
            foreach (var interfaceType in type.GetInterfaces())
            {
                props.AddRange(GetAllProperties(interfaceType));
            }

            var names = new List<PropertyInfo>(props.Count);
            var duplicates = new List<PropertyInfo>(props.Count);
            foreach (var p in props)
            {
                var duplicate = names.SingleOrDefault(n => n.Name == p.Name && n.PropertyType == p.PropertyType);
                if (duplicate != null)
                {
                    duplicates.Add(p);
                }
                else
                {
                    names.Add(p);
                }
            }

            foreach (var d in duplicates)
            {
                props.Remove(d);
            }

            return props;
        }

    }

    static class ExtensionMethods
    {


        public static T Construct<T>(this Type type)
        {
            var defaultConstructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { }, null);
            if (defaultConstructor != null)
            {
                return (T)defaultConstructor.Invoke(null);
            }

            return (T)Activator.CreateInstance(type);
        }

        /// <summary>
        /// Returns true if the type can be serialized as is.
        /// </summary>
        public static bool IsSimpleType(this Type type)
        {
            return (type == typeof(string) ||
                    type.IsPrimitive ||
                    type == typeof(decimal) ||
                    type == typeof(Guid) ||
                    type == typeof(DateTime) ||
                    type == typeof(TimeSpan) ||
                    type == typeof(DateTimeOffset) ||
                    type.IsEnum);
        }

        /// <summary>
        /// Takes the name of the given type and makes it friendly for serialization
        /// by removing problematic characters.
        /// </summary>
        public static string SerializationFriendlyName(this Type t)
        {
            return TypeToNameLookup.GetOrAdd(t, type =>
            {
                var index = t.Name.IndexOf('`');
                if (index >= 0)
                {
                    var result = t.Name.Substring(0, index) + "Of";
                    var args = t.GetGenericArguments();
                    for (var i = 0; i < args.Length; i++)
                    {
                        result += args[i].SerializationFriendlyName();
                        if (i != args.Length - 1)
                        {
                            result += "And";
                        }
                    }

                    if (args.Length == 2)
                    {
                        if (typeof(KeyValuePair<,>).MakeGenericType(args[0], args[1]) == t)
                        {
                            result = "NServiceBus." + result;
                        }
                    }

                    return result;
                }
                return type.Name;
            });
        }

        static byte[] MsPublicKeyToken = typeof(string).Assembly.GetName().GetPublicKeyToken();

        static bool IsClrType(byte[] a1)
        {
            IStructuralEquatable structuralEquatable = a1;
            return structuralEquatable.Equals(MsPublicKeyToken, StructuralComparisons.StructuralEqualityComparer);
        }

        static ConcurrentDictionary<Type, bool> IsSystemTypeCache = new ConcurrentDictionary<Type, bool>();

        public static bool IsSystemType(this Type type)
        {
            bool result;

            if (!IsSystemTypeCache.TryGetValue(type, out result))
            {
                var nameOfContainingAssembly = type.Assembly.GetName().GetPublicKeyToken();
                IsSystemTypeCache[type] = result = IsClrType(nameOfContainingAssembly);
            }

            return result;
        }

        public static bool IsNServiceBusMarkerInterface(this Type type)
        {
            return type == typeof(IMessage) ||
                   type == typeof(ICommand) ||
                   type == typeof(IEvent);
        }

        static ConcurrentDictionary<Type, string> TypeToNameLookup = new ConcurrentDictionary<Type, string>();

        static byte[] nsbPublicKeyToken = typeof(ExtensionMethods).Assembly.GetName().GetPublicKeyToken();

        public static bool IsFromParticularAssembly(this Type type)
        {
            return type.Assembly.GetName()
                .GetPublicKeyToken()
                .SequenceEqual(nsbPublicKeyToken);
        }
    }
}
