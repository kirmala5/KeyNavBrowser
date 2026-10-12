using System.Text.Json;

namespace KeyNavBrowser.Services;

/// <summary>
/// Простое файловое хранилище объектов в JSON: типизированная обёртка над
/// System.Text.Json с атомарной записью (запись во временный файл + переименование).
/// </summary>
public sealed class JsonStore<T> where T : class
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly Func<T> _fallbackFactory;

    public JsonStore(string fileName, Func<T> fallbackFactory)
    {
        AppPaths.EnsureRootExists();
        _path = Path.Combine(AppPaths.Root, fileName);
        _fallbackFactory = fallbackFactory;
    }

    public string FilePath => _path;

    public T Load()
    {
        try
        {
            if (!File.Exists(_path))
                return _fallbackFactory();

            string json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
                return _fallbackFactory();

            return JsonSerializer.Deserialize<T>(json, Options) ?? _fallbackFactory();
        }
        catch
        {
            // Повреждённый файл настроек не должен ронять браузер — начинаем с значений по умолчанию.
            return _fallbackFactory();
        }
    }

    public void Save(T value)
    {
        try
        {
            AppPaths.EnsureRootExists();
            string json = JsonSerializer.Serialize(value, Options);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Запись настроек — вспомогательная операция; ошибки диска игнорируем.
        }
    }
}