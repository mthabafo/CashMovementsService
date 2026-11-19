using System.Globalization;

namespace CashMovements.Client;

/// <summary>Thrown for a mistake in the command line; the message is shown with the usage text.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
/// A parsed command line: a command name, positional arguments and <c>--name value</c> options.
/// Deliberately tiny: four commands don't justify a parsing library.
/// </summary>
internal sealed class CommandLine
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedOptions = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string? command) => Command = command;

    public string? Command { get; }

    public static CommandLine Parse(string[] args)
    {
        var line = new CommandLine(args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null);

        for (var i = line.Command is null ? 0 : 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var name = arg[2..];
                if (name.Equals("help", StringComparison.OrdinalIgnoreCase))
                {
                    line._options[name] = "";
                    continue;
                }

                if (i + 1 >= args.Length)
                    throw new UsageException($"Option --{name} needs a value.");

                line._options[name] = args[++i];
            }
            else
            {
                line._positional.Add(arg);
            }
        }

        return line;
    }

    public bool HelpRequested => _options.ContainsKey("help");

    /// <summary>The positional argument at <paramref name="index"/>; <paramref name="name"/> is used in the error message.</summary>
    public string Required(int index, string name)
        => index < _positional.Count ? _positional[index] : throw new UsageException($"Missing <{name}>.");

    public string? Optional(int index) => index < _positional.Count ? _positional[index] : null;

    public string? Option(string name)
    {
        _usedOptions.Add(name);
        return _options.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>A date/time option. Without an offset it is taken as local time; a bare date means midnight.</summary>
    public DateTimeOffset? TimeOption(string name)
    {
        var text = Option(name);
        if (text is null)
            return null;

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value))
            return value;

        throw new UsageException($"--{name} '{text}' is not a date/time. Use e.g. 2026-11-01 or 2026-11-01T14:30 or 2026-11-01T12:30Z.");
    }

    /// <summary>Fails on extra positional arguments or options the command didn't ask for (catches typos).</summary>
    public void EnsureNothingElse(int expectedPositional)
    {
        if (_positional.Count > expectedPositional)
            throw new UsageException($"Unexpected argument '{_positional[expectedPositional]}'.");

        var unknown = _options.Keys.FirstOrDefault(k => !_usedOptions.Contains(k) && !k.Equals("help", StringComparison.OrdinalIgnoreCase));
        if (unknown is not null)
            throw new UsageException($"Unknown option --{unknown}.");
    }
}
