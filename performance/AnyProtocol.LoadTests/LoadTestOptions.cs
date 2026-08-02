namespace AnyProtocol.LoadTests;

public sealed record LoadTestOptions
{
    public string Transport { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string? Matrix { get; init; }
    public int PayloadBytes { get; init; } = 256;
    public int Concurrency { get; init; } = 1;
    public int Operations { get; init; } = 1000;
    public int WarmupOperations { get; init; } = 100;
    public string JsonPath { get; init; } = string.Empty;
    public string MarkdownPath { get; init; } = string.Empty;
    public Uri? RabbitMqUri { get; init; }
    public string? KafkaBootstrapServers { get; init; }
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public string? BaselinePath { get; init; }
    public double Threshold { get; init; } = 0.20;
    public bool ConfirmRegressions { get; init; }

    public void Validate()
    {
        if (Matrix is not null)
        {
            if (Matrix is not ("smoke" or "full"))
            {
                throw new ArgumentException("--matrix must be smoke or full.");
            }

            if (Matrix == "full" && RabbitMqUri is null)
            {
                throw new ArgumentException("--rabbitmq-uri is required for the full matrix.");
            }

            if (Matrix == "full" && string.IsNullOrWhiteSpace(KafkaBootstrapServers))
            {
                throw new ArgumentException("--kafka-bootstrap is required for the full matrix.");
            }
        }
        else if (Transport is not ("inmemory" or "rabbitmq" or "kafka"))
        {
            throw new ArgumentException("--transport must be inmemory, rabbitmq, or kafka.");
        }

        if (Matrix is null && Scenario is not ("event" or "request-reply" or "competing-consumers"))
        {
            throw new ArgumentException("--scenario must be event, request-reply, or competing-consumers.");
        }

        ValidatePositive(PayloadBytes, "--payload-bytes");
        ValidatePositive(Concurrency, "--concurrency");
        ValidatePositive(Operations, "--operations");
        ValidatePositive(WarmupOperations, "--warmup");
        ArgumentException.ThrowIfNullOrWhiteSpace(JsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(MarkdownPath);
        if (Threshold is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException("--threshold", "--threshold must be at least zero and less than one.");
        }
        if (Transport == "rabbitmq" &&
            (RabbitMqUri is null || !RabbitMqUri.IsAbsoluteUri || RabbitMqUri.Scheme is not ("amqp" or "amqps")))
        {
            throw new ArgumentException("--rabbitmq-uri must be an absolute amqp or amqps URI.");
        }

        if (Transport == "kafka" && string.IsNullOrWhiteSpace(KafkaBootstrapServers))
        {
            throw new ArgumentException("--kafka-bootstrap is required for Kafka.");
        }
    }

    public static LoadTestOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length;)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Arguments must be supplied as --name value pairs.");
            }

            if (args[index] == "--confirm-regressions")
            {
                flags.Add(args[index]);
                index++;
                continue;
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{args[index]} requires a value.");
            }

            values[args[index]] = args[index + 1];
            index += 2;
        }

        string Required(string key) => values.TryGetValue(key, out var value)
            ? value
            : throw new ArgumentException($"{key} is required.");
        int Integer(string key, int defaultValue) => values.TryGetValue(key, out var raw) && int.TryParse(raw, out var value)
            ? value
            : values.ContainsKey(key)
                ? throw new ArgumentException($"{key} must be an integer.")
                : defaultValue;

        var options = new LoadTestOptions
        {
            Transport = values.GetValueOrDefault("--transport", string.Empty).ToLowerInvariant(),
            Scenario = values.GetValueOrDefault("--scenario", string.Empty).ToLowerInvariant(),
            Matrix = values.GetValueOrDefault("--matrix")?.ToLowerInvariant(),
            PayloadBytes = Integer("--payload-bytes", 256),
            Concurrency = Integer("--concurrency", 1),
            Operations = Integer("--operations", 1000),
            WarmupOperations = Integer("--warmup", 100),
            JsonPath = Required("--json"),
            MarkdownPath = Required("--markdown"),
            RabbitMqUri = values.TryGetValue("--rabbitmq-uri", out var rabbit) && Uri.TryCreate(rabbit, UriKind.Absolute, out var uri) ? uri : null,
            KafkaBootstrapServers = values.GetValueOrDefault("--kafka-bootstrap"),
            BaselinePath = values.GetValueOrDefault("--baseline"),
            Threshold = values.TryGetValue("--threshold", out var threshold) && double.TryParse(
                threshold,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedThreshold)
                    ? parsedThreshold
                    : values.ContainsKey("--threshold")
                        ? throw new ArgumentException("--threshold must be a number.")
                        : 0.20,
            ConfirmRegressions = flags.Contains("--confirm-regressions")
        };
        options.Validate();
        return options;
    }

    public IReadOnlyList<LoadTestOptions> Expand()
    {
        Validate();
        if (Matrix is null)
        {
            return [this];
        }

        if (Matrix == "smoke")
        {
            return [this with { Matrix = null, Transport = "inmemory", Scenario = "request-reply", PayloadBytes = 256, Concurrency = 1 }];
        }

        var expanded = new List<LoadTestOptions>(81);
        foreach (var transport in new[] { "inmemory", "rabbitmq", "kafka" })
        foreach (var scenario in new[] { "event", "request-reply", "competing-consumers" })
        foreach (var payload in new[] { 256, 4096, 65536 })
        foreach (var concurrency in new[] { 1, 8, 32 })
        {
            expanded.Add(this with
            {
                Matrix = null,
                Transport = transport,
                Scenario = scenario,
                PayloadBytes = payload,
                Concurrency = concurrency
            });
        }

        return expanded;
    }

    private static void ValidatePositive(int value, string option)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(option, $"{option} must be positive.");
        }
    }
}
