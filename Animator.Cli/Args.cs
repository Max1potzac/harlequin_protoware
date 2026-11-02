namespace Animator.Cli;

// A command's words: the ones without a dash in order (Positional), and the options (--name value,
// or --name alone for a switch). Options may come anywhere; "--" ends them.
public sealed class Args
{
	private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
	public List<string> Positional { get; } = new();

	// switches: the options that take no value
	public Args(IEnumerable<string> words, params string[] switches)
	{
		var flags = new HashSet<string>(switches, StringComparer.OrdinalIgnoreCase);
		using var e = words.GetEnumerator();
		bool rest = false;
		while (e.MoveNext())
		{
			string w = e.Current;
			if (rest || !w.StartsWith("--", StringComparison.Ordinal) || w.Length == 2)
			{
				if (w == "--" && !rest) rest = true;
				else Positional.Add(w);
				continue;
			}
			string name = w[2..];
			string? value = null;
			int eq = name.IndexOf('=');
			if (eq >= 0)
			{
				value = name[(eq + 1)..];
				name = name[..eq];
			}
			else if (!flags.Contains(name))
			{
				if (!e.MoveNext()) throw new UsageException($"--{name} needs a value.");
				value = e.Current;
			}
			_options[name] = value;
		}
	}

	public bool Has(string name)
	{
		_used.Add(name);
		return _options.ContainsKey(name);
	}

	public string? Get(string name)
	{
		_used.Add(name);
		return _options.TryGetValue(name, out var v) ? v : null;
	}

	public string Need(string name) => Get(name) ?? throw new UsageException($"--{name} is needed.");

	public float Float(string name, float fallback)
	{
		if (Get(name) is not { } v) return fallback;
		return float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)
			? f : throw new UsageException($"--{name}: \"{v}\" isn't a number.");
	}

	public int Int(string name, int fallback)
	{
		if (Get(name) is not { } v) return fallback;
		return int.TryParse(v, out int i) ? i : throw new UsageException($"--{name}: \"{v}\" isn't a whole number.");
	}

	// A comma-separated list (empty when the option isn't there)
	public List<string> List(string name) =>
		(Get(name) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

	public string At(int index, string what) =>
		index < Positional.Count ? Positional[index] : throw new UsageException($"The {what} is missing.");

	// Options given that the command never asked about (most likely typos)
	public void NothingElse()
	{
		var unknown = _options.Keys.Where(k => !_used.Contains(k)).ToList();
		if (unknown.Count > 0) throw new UsageException($"Unknown option(s): {string.Join(", ", unknown.Select(k => "--" + k))}.");
	}
}

// Wrong words on the command line: the message, then the command's usage
public sealed class UsageException(string message) : Exception(message);
