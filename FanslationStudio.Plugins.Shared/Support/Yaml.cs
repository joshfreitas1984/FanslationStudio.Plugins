using System;
using System.Collections.Generic;
using System.Linq;
using SharpYaml;
using SharpYaml.Serialization;
using SharpYaml.Serialization.Descriptors;
using SharpYaml.Serialization.Serializers;

namespace FanslationStudio.Plugins.Support;

/// <summary>
/// Provides factory methods for creating YAML serializers with custom settings.
/// </summary>
public class Yaml
{
    /// <summary>
    /// Creates a serializer for writing objects to YAML format.
    /// Excludes members with default values and uses camelCase naming.
    /// </summary>
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

    /// <summary>
    /// Creates a serializer for reading YAML format into objects.
    /// Uses camelCase naming convention.
    /// </summary>
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

/// <summary>
/// Custom serializer backend that excludes members with default values from serialization.
/// Caches default instances of types for efficient comparison.
/// </summary>
public class DefaultValueExcludingBackend : IObjectSerializerBackend
{
    private readonly DefaultObjectSerializerBackend _defaultBackend = new DefaultObjectSerializerBackend();
    private readonly Dictionary<Type, object> _defaultInstances = new Dictionary<Type, object>();

    // Tracks whether the current member being written should be skipped
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
        // Determine if this member should be excluded from serialization
        // (e.g., if it has a null or default value)
        _skipCurrentMember = ShouldSkipMember(ref objectContext, member);

        if (!_skipCurrentMember)
        {
            _defaultBackend.WriteMemberName(ref objectContext, member, name);
        }
    }

    public void WriteMemberValue(ref ObjectContext objectContext, IMemberDescriptor member, object memberValue, Type memberType)
    {
        if (!_skipCurrentMember)
        {
            _defaultBackend.WriteMemberValue(ref objectContext, member, memberValue, memberType);
        }

        // Reset the skip flag for the next member
        _skipCurrentMember = false;
    }

    /// <summary>
    /// Determines if a member should be excluded from serialization.
    /// A member is skipped if it's null or equals the default value for that type.
    /// </summary>
    private bool ShouldSkipMember(ref ObjectContext objectContext, IMemberDescriptor member)
    {
        var memberValue = member.Get(objectContext.Instance);

        if (memberValue == null)
        {
            return true;
        }

        var objectType = objectContext.Instance.GetType();
        var defaultInstance = GetOrCreateDefaultInstance(objectType);

        if (defaultInstance != null)
        {
            var defaultValue = member.Get(defaultInstance);
            return Equals(memberValue, defaultValue);
        }

        return false;
    }

    /// <summary>
    /// Gets a cached default instance for the specified type, or creates and caches one if it doesn't exist.
    /// Returns null if the type cannot be instantiated (e.g., abstract, interface, or no default constructor).
    /// </summary>
    private object GetOrCreateDefaultInstance(Type type)
    {
        if (_defaultInstances.TryGetValue(type, out var defaultInstance))
        {
            return defaultInstance;
        }

        try
        {
            if (!type.IsAbstract && !type.IsInterface && HasDefaultConstructor(type))
            {
                defaultInstance = Activator.CreateInstance(type);
                _defaultInstances[type] = defaultInstance;
                return defaultInstance;
            }
        }
        catch
        {
            // If instantiation fails, cache null to avoid repeated attempts
            _defaultInstances[type] = null;
        }

        return null;
    }

    public void WriteCollectionItem(ref ObjectContext objectContext, object item, Type itemType, int index)
    {
        _defaultBackend.WriteCollectionItem(ref objectContext, item, itemType, index);
    }

    public void WriteDictionaryItem(ref ObjectContext objectContext, KeyValuePair<object, object> keyValue, KeyValuePair<Type, Type> types)
    {
        _defaultBackend.WriteDictionaryItem(ref objectContext, keyValue, types);
    }

    /// <summary>
    /// Checks if a type has a parameterless constructor.
    /// </summary>
    private bool HasDefaultConstructor(Type type)
    {
        return type.GetConstructor(Type.EmptyTypes) != null;
    }
}