using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OtogiTranslate
{
    // One dialogue line of a story response. Value is the live JSON node, so applying a
    // translation rewrites the response in place.
    internal sealed class SceneLine
    {
        internal int Index;
        internal JValue Value;
        internal string Speaker;
        internal string Name;
    }

    // Story responses reuse one line shape across ordinary, rich, adult, and world stories:
    // an object with a Phrase or Serif string, a Name, and a speaker id (MMonsterId, or the
    // talking entry of Characters). Walking the document keeps all of them on one path.
    internal sealed class SceneDocument
    {
        internal JToken Root;
        internal string Title;
        internal readonly List<SceneLine> Lines = new List<SceneLine>();

        internal static SceneDocument Parse(string json)
        {
            var document = new SceneDocument { Root = JToken.Parse(json) };
            document.Visit(document.Root);
            return document;
        }

        internal string ToJson()
        {
            return Root.ToString(Formatting.None);
        }

        private void Visit(JToken token)
        {
            var item = token as JObject;
            if (item != null)
            {
                var text = (item["Phrase"] ?? item["Serif"]) as JValue;
                if (text != null && text.Type == JTokenType.String)
                {
                    var name = item["Name"] as JValue;
                    var nameText = name != null && name.Type == JTokenType.String
                        ? (string)name.Value : string.Empty;
                    Lines.Add(new SceneLine
                    {
                        Index = Lines.Count,
                        Value = text,
                        Name = nameText,
                        Speaker = SpeakerKey(item, nameText)
                    });
                }
                var title = item["Title"] as JValue;
                if (Title == null && title != null && title.Type == JTokenType.String &&
                    !string.IsNullOrWhiteSpace((string)title.Value))
                    Title = (string)title.Value;
            }
            foreach (var child in token.Children())
                Visit(child);
        }

        private static string SpeakerKey(JObject item, string name)
        {
            var monster = MonsterId(item);
            if (monster == 0)
            {
                var characters = item["Characters"] as JArray;
                if (characters != null)
                {
                    foreach (var character in characters)
                    {
                        var entry = character as JObject;
                        if (entry != null && (bool?)entry["IsTalking"] == true)
                        {
                            monster = MonsterId(entry);
                            break;
                        }
                    }
                }
            }
            if (monster != 0)
                return "m" + monster.ToString(CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(name) ? null : "n" + name;
        }

        private static long MonsterId(JObject item)
        {
            var value = item["MMonsterId"] as JValue;
            return value != null && value.Type == JTokenType.Integer ? (long)value : 0;
        }
    }

    // One LLM request covering a contiguous range of a scene. The whole range goes out as
    // context with speakers, so tone and address stay consistent; only lines carrying an id
    // are translated.
    internal sealed class SceneBatch
    {
        internal const int ProtocolVersion = 1;
        internal const int MaxSourceCharacters = 8000;
        private const int MaxTranslationLength = 4000;

        internal string Type;
        internal string Id;
        internal string Payload;
        internal readonly List<KeyValuePair<int, string>> Targets =
            new List<KeyValuePair<int, string>>();

        internal string Key { get { return Type + "/" + Id; } }

        internal static List<SceneBatch> Create(
            string type, string id, SceneDocument document, ICollection<int> targets)
        {
            var batches = new List<SceneBatch>();
            var start = 0;
            while (start < document.Lines.Count)
            {
                var end = start;
                var characters = 0;
                while (end < document.Lines.Count &&
                    (end == start || characters + Length(document.Lines[end]) <= MaxSourceCharacters))
                {
                    characters += Length(document.Lines[end]);
                    end++;
                }
                var batch = Build(type, id, document, targets, start, end);
                if (batch != null)
                    batches.Add(batch);
                start = end;
            }
            return batches;
        }

        private static int Length(SceneLine line)
        {
            return ((string)line.Value.Value ?? string.Empty).Length + line.Name.Length;
        }

        private static SceneBatch Build(
            string type, string id, SceneDocument document, ICollection<int> targets,
            int start, int end)
        {
            var batch = new SceneBatch { Type = type, Id = id };
            var speakers = new Dictionary<string, string>(StringComparer.Ordinal);
            var lines = new JArray();
            for (var index = start; index < end; index++)
            {
                var line = document.Lines[index];
                var text = (string)line.Value.Value ?? string.Empty;
                var entry = new JObject();
                if (targets.Contains(index))
                {
                    entry["id"] = "t" + batch.Targets.Count.ToString(CultureInfo.InvariantCulture);
                    batch.Targets.Add(new KeyValuePair<int, string>(index, text));
                }
                string speaker = null;
                if (line.Speaker != null && !speakers.TryGetValue(line.Speaker, out speaker))
                {
                    speaker = "s" + (speakers.Count + 1).ToString(CultureInfo.InvariantCulture);
                    speakers.Add(line.Speaker, speaker);
                }
                entry["speaker"] = speaker;
                entry["name"] = line.Name;
                entry["text"] = text;
                lines.Add(entry);
            }
            if (batch.Targets.Count == 0)
                return null;

            var payload = new JObject();
            payload["version"] = ProtocolVersion;
            payload["scene"] = batch.Key;
            payload["title"] = document.Title;
            payload["lines"] = lines;
            batch.Payload = payload.ToString(Formatting.None);
            return batch;
        }

        // Returns false when the response structure does not match, which rejects the whole
        // batch. Otherwise every target gets an entry: a translation, or null when that single
        // line failed validation and belongs to the per-line path.
        internal bool TryParse(string content, out string[] translations)
        {
            translations = null;
            JObject root;
            try
            {
                root = JToken.Parse(StripCodeFence(content)) as JObject;
            }
            catch (JsonException)
            {
                return false;
            }
            var entries = root == null ? null : root["translations"] as JArray;
            if (entries == null || (int?)root["version"] != ProtocolVersion ||
                (string)root["scene"] != Key || entries.Count != Targets.Count)
                return false;

            var result = new string[Targets.Count];
            for (var index = 0; index < Targets.Count; index++)
            {
                var entry = entries[index] as JObject;
                if (entry == null || (string)entry["id"] !=
                    "t" + index.ToString(CultureInfo.InvariantCulture))
                    return false;
                var source = Targets[index].Value;
                var text = entry["text"] as JValue;
                var translated = text != null && text.Type == JTokenType.String
                    ? (string)text.Value : null;
                if (!string.IsNullOrWhiteSpace(translated) &&
                    translated.Length <= MaxTranslationLength &&
                    !string.Equals(source, translated, StringComparison.Ordinal) &&
                    RuntimeTranslator.HasSameProtectedTokens(source, translated))
                    result[index] = translated;
            }
            translations = result;
            return true;
        }

        private static string StripCodeFence(string content)
        {
            var trimmed = (content ?? string.Empty).Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
                return trimmed;
            var start = trimmed.IndexOf('\n');
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            return start >= 0 && end > start
                ? trimmed.Substring(start + 1, end - start - 1).Trim()
                : trimmed;
        }
    }

    // Per-scene LLM results keyed by line index. A null translation marks a line that failed
    // validation; it stays on the per-line path instead of being requested again.
    internal static class SceneCache
    {
        internal sealed class Entry
        {
            internal string Source;
            internal string Translation;
        }

        internal static Dictionary<int, Entry> Load(string path)
        {
            var result = new Dictionary<int, Entry>();
            if (!File.Exists(path))
                return result;
            try
            {
                var root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                var lines = root["lines"] as JObject;
                if ((int?)root["version"] != SceneBatch.ProtocolVersion || lines == null)
                    return result;
                foreach (var property in lines.Properties())
                {
                    int index;
                    var item = property.Value as JObject;
                    var source = item == null ? null : item["source"] as JValue;
                    if (!int.TryParse(property.Name, NumberStyles.None,
                            CultureInfo.InvariantCulture, out index) ||
                        source == null || source.Type != JTokenType.String)
                        continue;
                    var translation = item["translation"] as JValue;
                    var translated = translation != null && translation.Type == JTokenType.String
                        ? (string)translation.Value : null;
                    if (translated != null &&
                        !RuntimeTranslator.HasSameProtectedTokens((string)source.Value, translated))
                        continue;
                    result[index] = new Entry
                    {
                        Source = (string)source.Value,
                        Translation = translated
                    };
                }
            }
            catch (Exception exception)
            {
                if (!(exception is JsonException) && !(exception is IOException))
                    throw;
            }
            return result;
        }

        internal static void Save(string path, Dictionary<int, Entry> entries)
        {
            var lines = new JObject();
            var indexes = new List<int>(entries.Keys);
            indexes.Sort();
            foreach (var index in indexes)
            {
                var item = new JObject();
                item["source"] = entries[index].Source;
                item["translation"] = entries[index].Translation;
                lines[index.ToString(CultureInfo.InvariantCulture)] = item;
            }
            var root = new JObject();
            root["version"] = SceneBatch.ProtocolVersion;
            root["lines"] = lines;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, root.ToString(Formatting.None), new UTF8Encoding(false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temporary, path);
        }
    }
}
