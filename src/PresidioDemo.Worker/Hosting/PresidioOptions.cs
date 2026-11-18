using Microsoft.Extensions.Configuration;

namespace PresidioDemo.Worker.Hosting;

/// <summary>
/// The protection configuration files, bound from the Presidio section; relative paths resolve against the application folder.
/// </summary>
public sealed class PresidioOptions
{

    #region Fields and Constants

    /// <summary>
    /// The environments in which <see cref="DetectionLogValues"/> may take effect (WR-020): non-production only.
    /// </summary>
    public static readonly IReadOnlyList<string> ValuesEnvironments = Array.AsReadOnly(new[] { "Local", "Dev", "QA" });

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets or sets whether Presidio's predefined recognizers (email, phone, credit card, IP address, IBAN, spaCy PERSON,
    /// NRP, LOCATION and DATE_TIME, and the rest) run beside what each feed's recognizer file defines. Off in the base
    /// settings: only the file's custom recognizers and <c>&lt;entity_mapping&gt;</c> keys act, and a file that defines
    /// nothing publishes its input unchanged. On: the predefined recognizers run too, less any the file switches off, and
    /// their spans get their entity's anonymizer. An environment settings block or the <c>Presidio__DefaultDetection</c>
    /// environment variable sets it for one feed service. Read at startup.
    /// </summary>
    /// <value>True to run Presidio's predefined recognizers as well.</value>
    public bool DefaultDetection { get; set; }

    /// <summary>
    /// Gets or sets whether the detection log is written (WR-018). Off in the base settings and on in the Local environment
    /// (QF-26111601); an environment settings block or the <c>Presidio__DetectionLog</c> environment variable sets it for one
    /// feed service. Read at startup.
    /// </summary>
    /// <value>True to write one value-free line per decoded string.</value>
    public bool DetectionLog { get; set; }

    /// <summary>
    /// Gets or sets whether each detection log line also carries the actual decoded value (WR-020). On in the base settings
    /// (QF-26111601); it takes effect only with <see cref="DetectionLog"/> on, and only in <see cref="ValuesEnvironments"/>:
    /// elsewhere <see cref="ApplyValuesEnvironment"/> switches it off. Read at startup.
    /// </summary>
    /// <value>True to append the JSON-encoded decoded value to each detection log line.</value>
    public bool DetectionLogValues { get; set; }

    /// <summary>
    /// Gets or sets the Python virtual environment folder, or null for the project's src/.venv.
    /// </summary>
    /// <value>The folder that holds pyvenv.cfg, such as /opt/venv in the container image.</value>
    public string? PythonEnvironment { get; set; }

    /// <summary>
    /// Gets or sets the folder that holds one recognizer XML file per feed (WR-014). The files ship inside the image
    /// (PR-005); the folder is resolved against the application folder.
    /// </summary>
    /// <value>The folder path, by default <c>Configuration/recognizers</c>.</value>
    public string RecognizerDirectory { get; set; } = "Configuration/recognizers";

    /// <summary>
    /// Gets whether detection log lines carry decoded values: both <see cref="DetectionLog"/> and
    /// <see cref="DetectionLogValues"/> are on (WR-020).
    /// </summary>
    /// <value>True when values are appended to detection log lines.</value>
    public bool WritesValues => DetectionLog && DetectionLogValues;

    #endregion

    #region Methods

    /// <summary>
    /// Binds the Presidio section strictly and resolves the file paths.
    /// </summary>
    /// <param name="configuration">The environment-resolved configuration.</param>
    /// <param name="baseDirectory">The folder that relative paths resolve against.</param>
    /// <returns>The options with absolute paths.</returns>
    /// <exception cref="InvalidOperationException">The configured Python environment has no pyvenv.cfg.</exception>
    public static PresidioOptions Load(IConfiguration configuration, string baseDirectory)
    {
        var options = configuration.GetSection("Presidio").Get<PresidioOptions>(binder => binder.ErrorOnUnknownConfiguration = true) ?? new();
        options.RecognizerDirectory = Path.GetFullPath(options.RecognizerDirectory, baseDirectory);
        string application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)) + Path.DirectorySeparatorChar;
        if (!options.RecognizerDirectory.StartsWith(application, StringComparison.OrdinalIgnoreCase))
        {
            // Recognizer files ship only inside the reviewed image (PR-005), never from a mounted or other folder.
            throw new InvalidOperationException("Presidio:RecognizerDirectory must be a folder inside the application folder.");
        }
        if (string.IsNullOrWhiteSpace(options.PythonEnvironment))
        {
            options.PythonEnvironment = null;
        }
        else
        {
            options.PythonEnvironment = Path.GetFullPath(options.PythonEnvironment, baseDirectory);
            if (!File.Exists(Path.Combine(options.PythonEnvironment, "pyvenv.cfg")))
            {
                throw new InvalidOperationException("Presidio:PythonEnvironment has no pyvenv.cfg: " + options.PythonEnvironment);
            }
        }
        return options;
    }

    /// <summary>
    /// Switches <see cref="DetectionLogValues"/> off in an environment outside <see cref="ValuesEnvironments"/>, so values
    /// never reach those logs, without stopping the worker (QF-26111601; previously startup stopped). The name is the
    /// selected environment, so an unset <c>DOTNET_ENVIRONMENT</c> that falls back to Local counts.
    /// </summary>
    /// <param name="environment">The selected environment name.</param>
    /// <returns>True when the values flag was on and has been switched off; the caller logs a value-free warning.</returns>
    public bool ApplyValuesEnvironment(string environment)
    {
        if (!DetectionLogValues || ValuesEnvironments.Contains(environment, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        DetectionLogValues = false;
        return true;
    }

    #endregion
}
