namespace FanslationStudio.Plugins.Support;

public interface IYamlHelper
{
    string Serialize(object obj);

    T Deserialize<T>(string yaml);
}
