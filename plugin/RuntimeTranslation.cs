using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OtogiTranslate
{
    internal sealed class RuntimeConfig
    {
        internal bool Enable;
        internal string Endpoint = "http://10.0.2.2:11434/v1/chat/completions";
        internal string Model = "qwen2.5:7b";
        internal string ApiKey = string.Empty;
        internal int TimeoutSeconds = 30;
        internal int RetryCount = 2;
        internal int RequestsPerSecond = 2;
        internal int MaxQueue = 128;
        internal double ScanIntervalSeconds = 0.5;
        internal bool LogSeenText;

        internal static RuntimeConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path,
                    "# Restart the game after editing. Never share this file if ApiKey is set.\n" +
                    "[LLM]\n" +
                    "Enable = false\n" +
                    "Endpoint = http://10.0.2.2:11434/v1/chat/completions\n" +
                    "Model = qwen2.5:7b\n" +
                    "ApiKey =\n" +
                    "TimeoutSeconds = 30\n" +
                    "RetryCount = 2\n" +
                    "RequestsPerSecond = 2\n" +
                    "MaxQueue = 128\n\n" +
                    "[UI]\n" +
                    "ScanIntervalSeconds = 0.5\n" +
                    "LogSeenText = false\n",
                    new UTF8Encoding(false));
            }
            return Parse(File.ReadAllLines(path, Encoding.UTF8));
        }

        internal static RuntimeConfig Parse(IEnumerable<string> lines)
        {
            var result = new RuntimeConfig();
            var section = string.Empty;
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                var key = (section + "." + line.Substring(0, equals).Trim())
                    .ToLowerInvariant();
                var value = line.Substring(equals + 1).Trim();
                bool boolean;
                int integer;
                double number;
                switch (key)
                {
                    case "llm.enable":
                        if (TryBoolean(value, out boolean)) result.Enable = boolean;
                        break;
                    case "llm.endpoint":
                        result.Endpoint = value;
                        break;
                    case "llm.model":
                        result.Model = value;
                        break;
                    case "llm.apikey":
                        result.ApiKey = value;
                        break;
                    case "llm.timeoutseconds":
                        if (int.TryParse(value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out integer))
                            result.TimeoutSeconds = Clamp(integer, 1, 120);
                        break;
                    case "llm.retrycount":
                        if (int.TryParse(value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out integer))
                            result.RetryCount = Clamp(integer, 0, 5);
                        break;
                    case "llm.requestspersecond":
                        if (int.TryParse(value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out integer))
                            result.RequestsPerSecond = Clamp(integer, 1, 10);
                        break;
                    case "llm.maxqueue":
                        if (int.TryParse(value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out integer))
                            result.MaxQueue = Clamp(integer, 1, 1024);
                        break;
                    case "ui.scanintervalseconds":
                        if (double.TryParse(value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out number))
                            result.ScanIntervalSeconds = Math.Max(0.1, Math.Min(10, number));
                        break;
                    case "ui.logseentext":
                        if (TryBoolean(value, out boolean)) result.LogSeenText = boolean;
                        break;
                }
            }
            return result;
        }

        private static bool TryBoolean(string value, out bool result)
        {
            if (bool.TryParse(value, out result))
                return true;
            if (value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }
            if (value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }
            return false;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal sealed class RuntimeTranslator : IDisposable
    {
        private const int MaxTextLength = 1000;
        private const int MaxTranslationLength = 4000;
        private const int MaxCacheEntries = 10000;
        private const int MaxResponseCharacters = 32768;
        private const string SystemPrompt =
            "Translate Japanese game UI text into Simplified Chinese. Return only the translation. " +
            "Preserve all markup, escape sequences, placeholders, numbers, and line breaks exactly.";
        private static readonly Regex ProtectedToken = new Regex(
            @"<[^>]+>|\\[nrt]|%[A-Za-z_][A-Za-z0-9_]*|\{\d+(?::[^}]*)?\}|\r\n|\r|\n",
            RegexOptions.CultureInvariant);

        private readonly object stateLock = new object();
        private readonly Dictionary<string, string> cache =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> translatedValues =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> pending =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> rejected =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> logged = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> queue = new Queue<string>();
        private readonly string cachePath;
        private readonly Action<string> info;
        private readonly Action<string> warning;
        private string lastErrorType;
        private string lastRejectionType;

        internal RuntimeConfig Config { get; private set; }
        internal string ConfigPath { get; private set; }
        internal bool ShouldScan { get { return Config.Enable || Config.LogSeenText; } }

        internal RuntimeTranslator(
            string gameDirectory,
            Action<string> info,
            Action<string> warning)
        {
            this.info = info;
            this.warning = warning;
            ConfigPath = Path.Combine(gameDirectory, "OtogiTranslate.cfg");
            cachePath = Path.Combine(gameDirectory, "OtogiTranslate.cache.jsonl");
            Config = RuntimeConfig.Load(ConfigPath);
            LoadCache();
            if (Config.Enable)
            {
                Uri endpoint;
                if (!TryValidateEndpoint(Config.Endpoint, out endpoint) ||
                    string.IsNullOrWhiteSpace(Config.Model))
                    throw new InvalidDataException(
                        "LLM Endpoint/Model is invalid; use HTTPS or emulator host 10.0.2.2");
            }
        }

        internal string Observe(string source)
        {
            if (string.IsNullOrEmpty(source))
                return source;

            string translated;
            lock (stateLock)
            {
                if (translatedValues.Contains(source) || rejected.Contains(source))
                    return source;
                if (cache.TryGetValue(source, out translated))
                    return translated;
            }

            if (!IsTranslationCandidate(source))
                return source;
            if (Config.LogSeenText)
                LogOnce(source);
            if (Config.Enable)
            {
                lock (stateLock)
                {
                    if (!pending.Contains(source) && queue.Count < Config.MaxQueue)
                    {
                        pending.Add(source);
                        queue.Enqueue(source);
                    }
                }
            }
            return source;
        }

        internal bool TryTake(out string source)
        {
            lock (stateLock)
            {
                if (queue.Count == 0)
                {
                    source = null;
                    return false;
                }
                source = queue.Dequeue();
                return true;
            }
        }

        internal byte[] BuildRequestBody(string source)
        {
            var body = new JObject();
            body["model"] = Config.Model;
            body["temperature"] = 0;
            body["stream"] = false;
            body["messages"] = new JArray(
                new JObject(new JProperty("role", "system"),
                    new JProperty("content", SystemPrompt)),
                new JObject(new JProperty("role", "user"),
                    new JProperty("content", source)));
            return Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
        }

        internal void Accept(string source, string json)
        {
            string translated;
            try
            {
                translated = ParseResponse(json);
            }
            catch (Exception exception)
            {
                Reject(source, DescribeException(exception));
                Complete(source);
                return;
            }

            if (string.IsNullOrWhiteSpace(translated) ||
                translated.Length > MaxTranslationLength ||
                string.Equals(source, translated, StringComparison.Ordinal) ||
                !HasSameProtectedTokens(source, translated))
            {
                Reject(source, "invalid-response");
                Complete(source);
                return;
            }

            try
            {
                Store(source, translated);
                lastErrorType = null;
            }
            catch (Exception exception)
            {
                WarnOnce("accept-" + DescribeException(exception));
            }
            finally
            {
                Complete(source);
            }
        }

        internal void Fail(string source, string reason)
        {
            Complete(source);
            WarnOnce(reason);
        }

        internal static string ParseResponse(string json)
        {
            if (json == null || json.Length > MaxResponseCharacters)
                throw new InvalidDataException("LLM response is too large");
            var root = JObject.Parse(json);
            var content = root.SelectToken("choices[0].message.content");
            if (content == null || content.Type != JTokenType.String)
                throw new InvalidDataException("LLM response has no message content");
            return (string)content;
        }

        internal static bool IsTranslationCandidate(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
                return false;
            foreach (var character in text)
            {
                if ((character >= '\u3040' && character <= '\u30ff') ||
                    (character >= '\uff66' && character <= '\uff9f'))
                    return true;
            }
            return false;
        }

        internal static bool TryValidateEndpoint(string value, out Uri endpoint)
        {
            endpoint = null;
            Uri parsed;
            if (!Uri.TryCreate(value, UriKind.Absolute, out parsed))
                return false;
            if (parsed.Scheme == Uri.UriSchemeHttps ||
                (parsed.Scheme == Uri.UriSchemeHttp &&
                    (parsed.IsLoopback || parsed.Host == "10.0.2.2")))
            {
                endpoint = parsed;
                return true;
            }
            return false;
        }

        internal static bool HasSameProtectedTokens(string source, string translated)
        {
            var sourceTokens = ProtectedToken.Matches(source);
            var translatedTokens = ProtectedToken.Matches(translated);
            if (sourceTokens.Count != translatedTokens.Count)
                return false;
            for (var index = 0; index < sourceTokens.Count; index++)
            {
                if (!string.Equals(sourceTokens[index].Value,
                    translatedTokens[index].Value, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private void LoadCache()
        {
            if (!File.Exists(cachePath))
                return;
            foreach (var line in File.ReadLines(cachePath, Encoding.UTF8))
            {
                if (cache.Count >= MaxCacheEntries)
                    break;
                try
                {
                    var item = JObject.Parse(line);
                    var source = (string)item["source"];
                    var translated = (string)item["translation"];
                    if (!string.IsNullOrEmpty(source) &&
                        !string.IsNullOrEmpty(translated) &&
                        HasSameProtectedTokens(source, translated))
                    {
                        cache[source] = translated;
                        translatedValues.Add(translated);
                    }
                }
                catch
                {
                    // A partial final line after an interrupted append is safe to ignore.
                }
            }
        }

        private void Store(string source, string translated)
        {
            Exception cacheError = null;
            lock (stateLock)
            {
                if (cache.ContainsKey(source) || cache.Count >= MaxCacheEntries)
                    return;
                cache[source] = translated;
                translatedValues.Add(translated);
                var item = new JObject();
                item["source"] = source;
                item["translation"] = translated;
                try
                {
                    File.AppendAllText(cachePath,
                        item.ToString(Formatting.None) + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (Exception exception)
                {
                    cacheError = exception;
                }
            }
            info("[OtogiTranslate] llm-translated");
            if (cacheError != null)
                WarnOnce("cache-write-" + DescribeException(cacheError));
        }

        private void LogOnce(string text)
        {
            lock (logged)
            {
                if (logged.Count >= 512 || !logged.Add(text))
                    return;
            }
            var shown = text.Replace("\r", "\\r").Replace("\n", "\\n");
            if (shown.Length > 160)
                shown = shown.Substring(0, 160) + "...";
            info("[OtogiTranslate] tmp-text=\"" + shown.Replace("\"", "\\\"") + "\"");
        }

        private void Reject(string source, string type)
        {
            lock (stateLock)
                rejected.Add(source);
            if (string.Equals(lastRejectionType, type, StringComparison.Ordinal))
                return;
            lastRejectionType = type;
            warning("[OtogiTranslate] llm-rejected: " + type);
        }

        private void WarnOnce(string type)
        {
            if (string.Equals(lastErrorType, type, StringComparison.Ordinal))
                return;
            lastErrorType = type;
            warning("[OtogiTranslate] llm-error: " + type);
        }

        private static string DescribeException(Exception exception)
        {
            var result = new StringBuilder();
            for (var depth = 0; exception != null && depth < 3; depth++)
            {
                if (result.Length != 0)
                    result.Append(" -> ");
                result.Append(exception.GetType().Name);
                if (!string.IsNullOrWhiteSpace(exception.Message))
                {
                    var message = exception.Message.Replace("\r", " ").Replace("\n", " ");
                    if (message.Length > 200)
                        message = message.Substring(0, 200) + "...";
                    result.Append(": ").Append(message);
                }
                exception = exception.InnerException;
            }
            return result.ToString();
        }

        private void Complete(string source)
        {
            lock (stateLock)
                pending.Remove(source);
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                queue.Clear();
                pending.Clear();
                rejected.Clear();
            }
        }
    }
}
