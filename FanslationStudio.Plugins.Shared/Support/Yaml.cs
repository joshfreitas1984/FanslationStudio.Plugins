using System;
using System.Collections.Generic;
using System.Linq;
using SharpYaml;
using SharpYaml.Serialization;
using SharpYaml.Serialization.Descriptors;
using SharpYaml.Serialization.Serializers;

namespace FanslationStudio.Plugins.Support;

public class Yaml
{
    public static Serializer CreateSerializer()
    {
        var settings = new SerializerSettings
        {
            NamingConvention = new CamelCaseNamingConvention(),
            EmitDefaultValues = false,
            EmitTags = false,
            SortKeyForMapping = false,
            ComparerForKeySorting = null,
            ObjectSerializerBackend = new DefaultValueExcludingBackend()
        };

        return new Serializer(settings);
    }

    public static Serializer CreateDeserializer()
    {
        var settings = new SerializerSettings
        {
            NamingConvention = new CamelCaseNamingConvention(),
            EmitTags = false
        };

        return new Serializer(settings);
    }
}

public class DefaultValueExcludingBackend : IObjectSerializerBackend
{
    private readonly DefaultObjectSerializerBackend _defaultBackend = new DefaultObjectSerializerBackend();
    private readonly Dictionary<Type, object> _defaultInstances = new Dictionary<Type, object>();
    private bool _skipCurrentMember = false;

    public YamlStyle GetStyle(ref ObjectContext objectContext)
    {
        return _defaultBackend.GetStyle(ref objectContext);
    }

    public string ReadMemberName(ref ObjectContext objectContext, string name, out bool skipMember)
    {
        return _defaultBackend.ReadMemberName(ref objectContext, name, out skipMember);
    }

    public object ReadMemberValue(ref ObjectContext objectContext, IMemberDescriptor member, object memberValue, Type memberType)
    {
        return _defaultBackend.ReadMemberValue(ref objectContext, member, memberValue, memberType);
    }

    public object ReadCollectionItem(ref ObjectContext objectContext, object value, Type itemType, int index)
    {
        return _defaultBackend.ReadCollectionItem(ref objectContext, value, itemType, index);
    }

    public KeyValuePair<object, object> ReadDictionaryItem(ref ObjectContext objectContext, KeyValuePair<Type, Type> keyValueType)
    {
        return _defaultBackend.ReadDictionaryItem(ref objectContext, keyValueType);
    }

    public void WriteMemberName(ref ObjectContext objectContext, IMemberDescriptor member, string name)
    {
        // Check if we should skip this member
        _skipCurrentMember = ShouldSkipMember(ref objectContext, member);

        if (!_skipCurrentMember)
        {
            _defaultBackend.WriteMemberName(ref objectContext, member, name);
        }
    }

    public void WriteMemberValue(ref ObjectContext objectContext, IMemberDescriptor member, object memberValue, Type memberType)
    {
        // Only write if we didn't skip the member name
        if (!_skipCurrentMember)
        {
            _defaultBackend.WriteMemberValue(ref objectContext, member, memberValue, memberType);
        }

        // Reset the flag
        _skipCurrentMember = false;
    }

    private bool ShouldSkipMember(ref ObjectContext objectContext, IMemberDescriptor member)
    {
        // Get the member value
        var memberValue = member.Get(objectContext.Instance);

        // Skip if value is null
        if (memberValue == null)
        {
            return true;
        }

        var objectType = objectContext.Instance.GetType();

        // Get or create default instance for comparison
        if (!_defaultInstances.TryGetValue(objectType, out var defaultInstance))
        {
            try
            {
                if (!objectType.IsAbstract && !objectType.IsInterface && HasDefaultConstructor(objectType))
                {
                    defaultInstance = Activator.CreateInstance(objectType);
                    _defaultInstances[objectType] = defaultInstance;
                }
            }
            catch
            {
                // If we can't create a default instance, don't skip
                return false;
            }
        }

        if (defaultInstance != null)
        {
            // Get default value for this member
            var defaultValue = member.Get(defaultInstance);

            // Skip if value equals default
            return Equals(memberValue, defaultValue);
        }

        return false;
    }

    public void WriteCollectionItem(ref ObjectContext objectContext, object item, Type itemType, int index)
    {
        _defaultBackend.WriteCollectionItem(ref objectContext, item, itemType, index);
    }

    public void WriteDictionaryItem(ref ObjectContext objectContext, KeyValuePair<object, object> keyValue, KeyValuePair<Type, Type> types)
    {
        _defaultBackend.WriteDictionaryItem(ref objectContext, keyValue, types);
    }

    private bool HasDefaultConstructor(Type type)
    {
        return type.GetConstructor(Type.EmptyTypes) != null;
    }
}