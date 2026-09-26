using FanslationStudio.Plugins.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FanslationStudio.Plugins.Support;

/// <summary>
/// A folder of YAML files, each a list of path-keyed contracts.
///
///   * Files load in alphabetical order; the first entry for a path wins.
///   * Each entry remembers the file it came from. Saving or deleting rewrites only that file
///     (new entries go to <c>defaultFileName</c>), so hand-organised files stay intact.
///   * <see cref="Find"/> resolves a hierarchy path: exact match first, then the first wildcard
///     entry (in load order) that matches, via <see cref="PathPattern"/>. Results are cached.
///
/// Pure .NET - no Unity calls - so it is safe in Shared and unit-testable.
/// </summary>
public class ContractRepository<T> where T : class
{
    private readonly string _folder;
    private readonly string _defaultFile;
    private readonly IYamlHelper _yamlHelper;
    private readonly IPluginLogger _logger;
    private readonly Func<T, string> _getPath;

    private readonly Dictionary<string, T> _entries = new Dictionary<string, T>();
    private readonly Dictionary<string, string> _sourceFiles = new Dictionary<string, string>();
    // Explicit order: Dictionary enumeration order is not preserved once entries are removed.
    private readonly List<string> _order = new List<string>();
    private readonly Dictionary<string, T> _matchCache = new Dictionary<string, T>();

    public ContractRepository(string folder, string defaultFileName, IYamlHelper yamlHelper, IPluginLogger logger, Func<T, string> getPath)
    {
        _folder = folder;
        _defaultFile = Path.Combine(folder, defaultFileName);
        _yamlHelper = yamlHelper;
        _logger = logger;
        _getPath = getPath;
    }

    public string Folder => _folder;

    /// <summary>Incremented on every change, so UIs and appliers can cheaply detect staleness.</summary>
    public int Version { get; private set; }

    public int Count => _order.Count;

    public IReadOnlyList<T> All => _order.Select(path => _entries[path]).ToList();

    public string GetSourceFile(string path)
    {
        return path != null && _sourceFiles.TryGetValue(path, out var file) ? file : null;
    }

    public void Load()
    {
        _entries.Clear();
        _sourceFiles.Clear();
        _order.Clear();
        _matchCache.Clear();

        if (!Directory.Exists(_folder))
            Directory.CreateDirectory(_folder);

        foreach (var file in Directory.EnumerateFiles(_folder, "*.yaml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var content = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                var contracts = _yamlHelper.Deserialize<List<T>>(content);
                if (contracts == null)
                    continue;

                foreach (var contract in contracts)
                {
                    var path = contract == null ? null : _getPath(contract);
                    if (string.IsNullOrEmpty(path))
                    {
                        _logger?.LogWarning($"Skipping entry without a path in '{file}'");
                        continue;
                    }

                    if (_entries.ContainsKey(path))
                        continue;

                    _entries[path] = contract;
                    _sourceFiles[path] = file;
                    _order.Add(path);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error loading '{file}': {ex.ToFullString()}");
            }
        }

        Version++;
    }

    public T Get(string path)
    {
        return path != null && _entries.TryGetValue(path, out var contract) ? contract : null;
    }

    public T Find(string path)
    {
        if (path == null)
            return null;

        if (_entries.TryGetValue(path, out var exact))
            return exact;

        if (_matchCache.TryGetValue(path, out var cached))
            return cached;

        T match = null;
        foreach (var pattern in _order)
        {
            if (PathPattern.IsWildcard(pattern) && PathPattern.IsMatch(pattern, path))
            {
                match = _entries[pattern];
                break;
            }
        }

        _matchCache[path] = match;
        return match;
    }

    /// <summary>Changes an entry in memory only (no file write), e.g. while the user edits it.</summary>
    public void Preview(T contract)
    {
        var path = _getPath(contract);
        if (!_entries.ContainsKey(path))
            _order.Add(path);
        _entries[path] = contract;
        _matchCache.Clear();
        Version++;
    }

    /// <summary>Drops an entry that only exists as a preview (never saved). Saved entries are kept.</summary>
    public bool DiscardPreview(string path)
    {
        if (path == null || !_entries.ContainsKey(path) || _sourceFiles.ContainsKey(path))
            return false;

        RemoveEntry(path);
        _matchCache.Clear();
        Version++;
        return true;
    }

    /// <summary>
    /// Creates or updates an entry and rewrites the file that owns it.
    /// </summary>
    /// <param name="previousPath">The path the entry was selected under, if the path was edited
    /// (e.g. to add wildcards), so the old entry is replaced rather than duplicated.</param>
    /// <returns>The file that was written.</returns>
    public string Save(T contract, string previousPath = null)
    {
        var path = _getPath(contract);
        string previousFile = null;

        if (!string.IsNullOrEmpty(previousPath) && previousPath != path && _entries.ContainsKey(previousPath))
        {
            _sourceFiles.TryGetValue(previousPath, out previousFile);
            var index = _order.IndexOf(previousPath);
            RemoveEntry(previousPath);

            // Keep the renamed entry where the old one was, unless the new path already exists.
            if (!_entries.ContainsKey(path) && index >= 0)
                _order.Insert(index, path);
        }

        if (!_entries.ContainsKey(path) && !_order.Contains(path))
            _order.Add(path);
        _entries[path] = contract;

        if (!_sourceFiles.TryGetValue(path, out var file))
        {
            file = previousFile ?? _defaultFile;
            _sourceFiles[path] = file;
        }

        _matchCache.Clear();
        Version++;

        RewriteFile(file);
        if (previousFile != null && previousFile != file)
            RewriteFile(previousFile);

        return file;
    }

    public bool Delete(string path)
    {
        if (path == null || !_entries.ContainsKey(path))
            return false;

        _sourceFiles.TryGetValue(path, out var file);
        RemoveEntry(path);
        _matchCache.Clear();
        Version++;

        if (file != null)
            RewriteFile(file);

        return true;
    }

    private void RemoveEntry(string path)
    {
        _entries.Remove(path);
        _sourceFiles.Remove(path);
        _order.Remove(path);
    }

    private void RewriteFile(string file)
    {
        var contracts = _order
            .Where(path => _sourceFiles.TryGetValue(path, out var owner) && owner == file)
            .Select(path => _entries[path])
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(file));
        File.WriteAllText(file, contracts.Count > 0 ? _yamlHelper.Serialize(contracts) : string.Empty);
    }
}
