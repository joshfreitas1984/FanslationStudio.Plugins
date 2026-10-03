using FanslationStudio.Plugins.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FanslationStudio.Plugins.Support;

/// <summary>
/// A folder of YAML files, each a list of contracts matched against hierarchy paths.
///
///   * Each contract has a path pattern (matched with <see cref="PathPattern"/>) and a unique key.
///     The key is the path unless a key selector is given - e.g. sprite rules allow several
///     rules for one path, told apart by which sprite they replace.
///   * Files load in alphabetical order; the first entry for a key wins.
///   * Each entry remembers the file it came from. Saving or deleting rewrites only that file
///     (new entries go to <c>defaultFileName</c>), so hand-organised files stay intact.
///   * <see cref="FindCandidates"/> lists the entries matching a path: exact patterns first, then
///     wildcards, each in load order. <see cref="Find"/> is the first candidate. Cached.
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
    private readonly Func<T, string> _getKey;

    private readonly Dictionary<string, T> _entries = new Dictionary<string, T>();
    private readonly Dictionary<string, string> _sourceFiles = new Dictionary<string, string>();
    // Explicit order: Dictionary enumeration order is not preserved once entries are removed.
    private readonly List<string> _order = new List<string>();
    private readonly Dictionary<string, IReadOnlyList<T>> _matchCache = new Dictionary<string, IReadOnlyList<T>>();

    // Lookup index, rebuilt lazily after any change: exact patterns by path, and the wildcard
    // entries alone, so a cache miss costs O(wildcards) rather than O(all entries).
    private readonly Dictionary<string, List<T>> _exactByPath = new Dictionary<string, List<T>>();
    private readonly List<KeyValuePair<string, T>> _wildcards = new List<KeyValuePair<string, T>>();
    // Names an element must have to match some entry (see PathPattern.LeafNameOf); unusable
    // when any entry's last segment is a wildcard.
    private readonly HashSet<string> _leafNames = new HashSet<string>(StringComparer.Ordinal);
    private bool _anyWildcardLeaf;
    private bool _indexDirty = true;

    // Paths are cached per distinct path, and some games generate endless unique names
    // (numbered clones), so the cache is dropped once it gets this big.
    private const int MaxCachedPaths = 20000;
    private static readonly T[] NoMatches = new T[0];

    public ContractRepository(string folder, string defaultFileName, IYamlHelper yamlHelper, IPluginLogger logger,
        Func<T, string> getPath, Func<T, string> getKey = null)
    {
        _folder = folder;
        _defaultFile = Path.Combine(folder, defaultFileName);
        _yamlHelper = yamlHelper;
        _logger = logger;
        _getPath = getPath;
        _getKey = getKey ?? getPath;
    }

    public string Folder => _folder;

    /// <summary>Incremented on every change, so UIs and appliers can cheaply detect staleness.</summary>
    public int Version { get; private set; }

    public int Count => _order.Count;

    public IReadOnlyList<T> All => _order.Select(key => _entries[key]).ToList();

    public string KeyOf(T contract) => _getKey(contract);

    public string GetSourceFile(string key)
    {
        return key != null && _sourceFiles.TryGetValue(key, out var file) ? file : null;
    }

    public void Load()
    {
        _entries.Clear();
        _sourceFiles.Clear();
        _order.Clear();
        Invalidate();

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
                    if (contract == null || string.IsNullOrEmpty(_getPath(contract)))
                    {
                        _logger?.LogWarning($"Skipping entry without a path in '{file}'");
                        continue;
                    }

                    var key = _getKey(contract);
                    if (_entries.ContainsKey(key))
                        continue;

                    _entries[key] = contract;
                    _sourceFiles[key] = file;
                    _order.Add(key);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error loading '{file}': {ex.ToFullString()}");
            }
        }

        Version++;
    }

    private void Invalidate()
    {
        _matchCache.Clear();
        _indexDirty = true;
    }

    private void EnsureIndex()
    {
        if (!_indexDirty)
            return;

        _indexDirty = false;
        _exactByPath.Clear();
        _wildcards.Clear();
        _leafNames.Clear();
        _anyWildcardLeaf = false;

        foreach (var key in _order)
        {
            var contract = _entries[key];
            var pattern = _getPath(contract);
            if (pattern == null)
                continue;

            if (PathPattern.IsWildcard(pattern))
                _wildcards.Add(new KeyValuePair<string, T>(pattern, contract));

            // Exact patterns are also looked up literally: a pattern can equal the path itself.
            if (!_exactByPath.TryGetValue(pattern, out var list))
                _exactByPath[pattern] = list = new List<T>(1);
            list.Add(contract);

            var leaf = PathPattern.LeafNameOf(pattern);
            if (leaf == null)
                _anyWildcardLeaf = true;
            else
                _leafNames.Add(leaf);
        }
    }

    /// <summary>
    /// False only when no entry can match a path whose last segment is <paramref name="name"/>,
    /// so callers on hot hooks can skip building the path at all.
    /// </summary>
    public bool CouldMatchName(string name)
    {
        if (_order.Count == 0 || name == null)
            return false;

        EnsureIndex();
        if (_anyWildcardLeaf)
            return true;

        // A name containing '/' (e.g. "HP/MP") makes the path's last segment only its tail.
        var slash = name.LastIndexOf('/');
        return _leafNames.Contains(slash < 0 ? name : name.Substring(slash + 1));
    }

    public T Get(string key)
    {
        return key != null && _entries.TryGetValue(key, out var contract) ? contract : null;
    }

    public T Find(string path)
    {
        var candidates = FindCandidates(path);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>Every entry whose pattern matches the path: exact patterns first, then wildcards.</summary>
    public IReadOnlyList<T> FindCandidates(string path)
    {
        if (path == null || _order.Count == 0)
            return NoMatches;

        if (_matchCache.TryGetValue(path, out var cached))
            return cached;

        EnsureIndex();
        List<T> result = null;
        if (_exactByPath.TryGetValue(path, out var exact))
            result = new List<T>(exact);

        foreach (var wildcard in _wildcards)
        {
            // The literal-equality case was already added from _exactByPath.
            if (wildcard.Key != path && PathPattern.IsMatch(wildcard.Key, path))
                (result ??= new List<T>()).Add(wildcard.Value);
        }

        if (_matchCache.Count >= MaxCachedPaths)
            _matchCache.Clear();

        IReadOnlyList<T> matches = result ?? (IReadOnlyList<T>)NoMatches;
        _matchCache[path] = matches;
        return matches;
    }

    /// <summary>Changes an entry in memory only (no file write), e.g. while the user edits it.</summary>
    public void Preview(T contract)
    {
        var key = _getKey(contract);
        if (!_entries.ContainsKey(key))
            _order.Add(key);
        _entries[key] = contract;
        Invalidate();
        Version++;
    }

    /// <summary>Drops an entry that only exists as a preview (never saved). Saved entries are kept.</summary>
    public bool DiscardPreview(string key)
    {
        if (key == null || !_entries.ContainsKey(key) || _sourceFiles.ContainsKey(key))
            return false;

        RemoveEntry(key);
        Invalidate();
        Version++;
        return true;
    }

    /// <summary>
    /// Creates or updates an entry and rewrites the file that owns it.
    /// </summary>
    /// <param name="previousKey">The key the entry was selected under, if its path (or key) was
    /// edited, e.g. to add wildcards, so the old entry is replaced rather than duplicated.</param>
    /// <returns>The file that was written.</returns>
    public string Save(T contract, string previousKey = null)
    {
        var key = _getKey(contract);
        string previousFile = null;

        if (!string.IsNullOrEmpty(previousKey) && previousKey != key && _entries.ContainsKey(previousKey))
        {
            _sourceFiles.TryGetValue(previousKey, out previousFile);
            var index = _order.IndexOf(previousKey);
            RemoveEntry(previousKey);

            // Keep the renamed entry where the old one was, unless the new key already exists.
            if (!_entries.ContainsKey(key) && index >= 0)
                _order.Insert(index, key);
        }

        if (!_entries.ContainsKey(key) && !_order.Contains(key))
            _order.Add(key);
        _entries[key] = contract;

        if (!_sourceFiles.TryGetValue(key, out var file))
        {
            file = previousFile ?? _defaultFile;
            _sourceFiles[key] = file;
        }

        Invalidate();
        Version++;

        RewriteFile(file);
        if (previousFile != null && previousFile != file)
            RewriteFile(previousFile);

        return file;
    }

    public bool Delete(string key)
    {
        if (key == null || !_entries.ContainsKey(key))
            return false;

        _sourceFiles.TryGetValue(key, out var file);
        RemoveEntry(key);
        Invalidate();
        Version++;

        if (file != null)
            RewriteFile(file);

        return true;
    }

    private void RemoveEntry(string key)
    {
        _entries.Remove(key);
        _sourceFiles.Remove(key);
        _order.Remove(key);
    }

    private void RewriteFile(string file)
    {
        var contracts = _order
            .Where(key => _sourceFiles.TryGetValue(key, out var owner) && owner == file)
            .Select(key => _entries[key])
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(file));
        File.WriteAllText(file, contracts.Count > 0 ? _yamlHelper.Serialize(contracts) : string.Empty);
    }
}
