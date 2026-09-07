using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#if !SELF_TEST
using MelonLoader;

[assembly: MelonInfo(typeof(OtogiTranslate.OtogiTranslatePlugin), "Otogi Translate", "0.6.0", "OtogiTranslate")]
#endif

namespace OtogiTranslate
{
    internal static class TranslationLogic
    {
        internal static Dictionary<string, string> ParseDictionary(string json)
        {
            var root = JToken.Parse(json.TrimStart('\ufeff')) as JObject;
            if (root == null)
                throw new InvalidDataException("translation JSON must be an object");

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in root.Properties())
            {
                if (property.Value.Type == JTokenType.String)
                    result[property.Name] = (string)property.Value;
            }

            foreach (var property in root.Properties())
            {
                var normalized = NormalizeSource(property.Name);
                if (property.Value.Type == JTokenType.String && !result.ContainsKey(normalized))
                    result[normalized] = (string)property.Value;
            }
            return result;
        }

        internal static string TranslateJson(
            string originalJson,
            Dictionary<string, string> dictionary,
            out int replacements)
        {
            var root = JToken.Parse(originalJson);
            replacements = ReplaceStrings(root, dictionary);
            return replacements == 0 ? originalJson : root.ToString(Formatting.None);
        }

        internal static bool IsFontUrl(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                uri.AbsolutePath.TrimEnd('/').EndsWith(
                    "/Assets/font", StringComparison.OrdinalIgnoreCase);
        }

        private static int ReplaceStrings(JToken token, Dictionary<string, string> dictionary)
        {
            var value = token as JValue;
            if (value != null && value.Type == JTokenType.String)
            {
                var source = (string)value.Value;
                var normalized = NormalizeSource(source);
                string translated;
                if (!dictionary.TryGetValue(source, out translated) &&
                    !dictionary.TryGetValue(normalized, out translated))
                    translated = normalized;

                translated = NormalizeTranslation(translated, dictionary);
                if (!string.Equals(source, translated, StringComparison.Ordinal))
                {
                    value.Value = translated;
                    return 1;
                }
                return 0;
            }

            var count = 0;
            foreach (var child in token.Children())
                count += ReplaceStrings(child, dictionary);
            return count;
        }

        private static string NormalizeSource(string value)
        {
            return value
                .Replace("%user_name", "人間さん")
                .Replace("人間さん先生", "人間さん")
                .Replace("人間さんさん", "人間さん")
                .Replace("\\n", " ");
        }

        private static string NormalizeTranslation(
            string value,
            Dictionary<string, string> dictionary)
        {
            string userName;
            if (!dictionary.TryGetValue("人間さん", out userName))
                userName = "人間さん";
            return value.Replace("%user_name", userName).Replace("\\n", " ");
        }
    }

#if SELF_TEST
    public static class TranslationSelfTest
    {
        public static int Main()
        {
            var dictionary = TranslationLogic.ParseDictionary(
                "{\"人間さん\":\"人间君\",\"変帝＆%user_name\":\"怪帝＆%user_name\",\"こんにちは\":\"你好\"}");
            int replacements;
            var output = TranslationLogic.TranslateJson(
                "{\"line\":\"変帝＆%user_name\",\"nested\":[\"こんにちは\",\"未翻訳\"]}",
                dictionary,
                out replacements);
            var parsed = JObject.Parse(output);
            var runtimeConfig = RuntimeConfig.Parse(new[]
            {
                "[LLM]", "Enable = true", "RetryCount = 0", "MaxQueue = 9999",
                "[UI]", "ScanIntervalSeconds = 0.01"
            });
            Uri endpoint;
            if (replacements != 2 ||
                (string)parsed["line"] != "怪帝＆人间君" ||
                (string)parsed["nested"][0] != "你好" ||
                (string)parsed["nested"][1] != "未翻訳" ||
                !TranslationLogic.IsFontUrl("https://example.test/Assets/font?v=1") ||
                TranslationLogic.IsFontUrl("https://example.test/Assets/fonts") ||
                !runtimeConfig.Enable || runtimeConfig.RetryCount != 0 ||
                runtimeConfig.MaxQueue != 1024 || runtimeConfig.ScanIntervalSeconds != 0.1 ||
                !RuntimeTranslator.IsTranslationCandidate("こんにちは") ||
                RuntimeTranslator.IsTranslationCandidate("你好") ||
                !RuntimeTranslator.TryValidateEndpoint(
                    "http://10.0.2.2:11434/v1/chat/completions", out endpoint) ||
                RuntimeTranslator.TryValidateEndpoint(
                    "http://example.test/v1/chat/completions", out endpoint) ||
                !RuntimeTranslator.HasSameProtectedTokens(
                    "<color=red>こんにちは</color>", "<color=red>你好</color>") ||
                RuntimeTranslator.HasSameProtectedTokens(
                    "<color=red>こんにちは</color>", "你好") ||
                !RuntimeTranslator.HasSameProtectedTokens("あ\nい", "甲\n乙") ||
                RuntimeTranslator.HasSameProtectedTokens("あ\nい", "甲乙"))
                throw new InvalidOperationException("translation self-check failed");
            CheckRuntimeQueue();
            Console.WriteLine("PASS translation self-check");
            return 0;
        }

        private static void CheckRuntimeQueue()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "otogi-translate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(
                    Path.Combine(directory, "OtogiTranslate.cfg"),
                    "[LLM]\nEnable=true\nEndpoint=https://example.test/v1/chat/completions\n" +
                    "Model=test\nMaxQueue=1\n",
                    new UTF8Encoding(false));
                using (var translator = new RuntimeTranslator(
                    directory, ignored => { }, ignored => { }))
                {
                    const string source = "<b>こんにちは</b>";
                    string queued;
                    if (translator.Observe(source) != source ||
                        !translator.TryTake(out queued) || queued != source)
                        throw new InvalidOperationException("runtime queue self-check failed");
                    var request = JObject.Parse(
                        Encoding.UTF8.GetString(translator.BuildRequestBody(source)));
                    if ((string)request["model"] != "test")
                        throw new InvalidOperationException("runtime request self-check failed");
                    translator.Accept(
                        source,
                        "{\"choices\":[{\"message\":{\"content\":\"<b>・你好</b>\"}}]}");
                    if (translator.Observe(source) != "<b>・你好</b>" ||
                        translator.Observe("<b>・你好</b>") != "<b>・你好</b>" ||
                        translator.TryTake(out queued))
                        throw new InvalidOperationException("runtime cache self-check failed");
                    const string rejected = "さようなら";
                    translator.Observe(rejected);
                    if (!translator.TryTake(out queued) || queued != rejected)
                        throw new InvalidOperationException("runtime rejection queue self-check failed");
                    translator.Accept(rejected,
                        "{\"choices\":[{\"message\":{\"content\":\"さようなら\"}}]}");
                    translator.Observe(rejected);
                    if (translator.TryTake(out queued))
                        throw new InvalidOperationException("runtime rejection self-check failed");
                }

                var cachePath = Path.Combine(directory, "OtogiTranslate.cache.jsonl");
                File.Delete(cachePath);
                Directory.CreateDirectory(cachePath);
                using (var translator = new RuntimeTranslator(
                    directory, ignored => { }, ignored => { }))
                {
                    const string source = "おはよう";
                    string queued;
                    translator.Observe(source);
                    if (!translator.TryTake(out queued) || queued != source)
                        throw new InvalidOperationException("cache-error queue self-check failed");
                    translator.Accept(source,
                        "{\"choices\":[{\"message\":{\"content\":\"早上好\"}}]}");
                    if (translator.Observe(source) != "早上好")
                        throw new InvalidOperationException("cache-error fallback self-check failed");
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
#else
    public sealed class OtogiTranslatePlugin : MelonPlugin
    {
        private const string RemoteRoot =
            "https://raw.githubusercontent.com/alex343425/otogitranslate/refs/heads/main";
        private const string FontUrl =
            "https://r2.ntr.best/font/otogi-font";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DomainGet();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DomainAssemblyOpen(IntPtr domain, [MarshalAs(UnmanagedType.LPStr)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr AssemblyGetImage(IntPtr assembly);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ClassFromName(
            IntPtr image,
            [MarshalAs(UnmanagedType.LPStr)] string namespaze,
            [MarshalAs(UnmanagedType.LPStr)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ClassGetMethodFromName(
            IntPtr klass,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            int argumentCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ClassGetMethods(IntPtr klass, ref IntPtr iterator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr MethodGetName(IntPtr method);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint MethodGetParamCount(IntPtr method);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr MethodGetParam(IntPtr method, uint index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr TypeGetName(IntPtr type);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ClassGetType(IntPtr klass);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr TypeGetObject(IntPtr type);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ClassGetFieldFromName(
            IntPtr klass,
            [MarshalAs(UnmanagedType.LPStr)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ClassGetParent(IntPtr klass);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ObjectGetClass(IntPtr instance);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ObjectNew(IntPtr klass);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FieldGetValue(IntPtr instance, IntPtr field, IntPtr output);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr RuntimeInvoke(
            IntPtr method,
            IntPtr instance,
            IntPtr parameters,
            ref IntPtr exception);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StringLength(IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr StringChars(IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr StringNewUtf16(IntPtr value, int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate UIntPtr ArrayLength(IntPtr array);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ArrayNew(IntPtr elementClass, UIntPtr length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ObjectUnbox(IntPtr instance);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GcHandleNew(
            IntPtr instance,
            [MarshalAs(UnmanagedType.I1)] bool pinned);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GcHandleGetTarget(uint handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GcHandleFree(uint handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetCorlib();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DataAsTextGetter(IntPtr instance, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void TargetFrameRateSetter(int value, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr AtlasGetter(IntPtr instance, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void UnityUpdate(IntPtr instance, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void HttpRequestConstructor(
            IntPtr instance,
            IntPtr uri,
            IntPtr argument1,
            IntPtr argument2,
            IntPtr argument3,
            IntPtr argument4,
            IntPtr methodInfo);

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> Cache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private static readonly Queue<string> DictionaryDownloads = new Queue<string>();
        private static readonly HashSet<string> PendingDictionaries =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> UnavailableDictionaries =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> DictionaryAttempts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> AdultDictionaryIds =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static ClassGetMethodFromName _classGetMethod;
        private static ClassGetMethods _classGetMethods;
        private static MethodGetName _methodGetName;
        private static MethodGetParamCount _methodGetParamCount;
        private static MethodGetParam _methodGetParam;
        private static TypeGetName _typeGetName;
        private static ClassGetType _classGetType;
        private static TypeGetObject _typeGetObject;
        private static ClassGetFieldFromName _classGetField;
        private static ClassGetParent _classGetParent;
        private static ObjectGetClass _objectGetClass;
        private static ObjectNew _objectNew;
        private static FieldGetValue _fieldGetValue;
        private static RuntimeInvoke _runtimeInvoke;
        private static StringLength _stringLength;
        private static StringChars _stringChars;
        private static StringNewUtf16 _stringNewUtf16;
        private static ArrayLength _arrayLength;
        private static ArrayNew _arrayNew;
        private static ObjectUnbox _objectUnbox;
        private static GcHandleNew _gcHandleNew;
        private static GcHandleGetTarget _gcHandleGetTarget;
        private static GcHandleFree _gcHandleFree;
        private static DataAsTextGetter _original;
        private static DataAsTextGetter _detour;
        private static IntPtr _targetSlot;
        private static TargetFrameRateSetter _originalFrameRate;
        private static TargetFrameRateSetter _frameRateDetour;
        private static IntPtr _frameRateTargetSlot;
        private static AtlasGetter _originalAtlas;
        private static AtlasGetter _atlasDetour;
        private static IntPtr _atlasTargetSlot;
        private static IntPtr _materialGetShader;
        private static IntPtr _shaderGetName;
        private static IntPtr _materialSetFloat;
        private static HttpRequestConstructor _originalRequestConstructor;
        private static HttpRequestConstructor _requestConstructorDetour;
        private static IntPtr _requestConstructorTargetSlot;
        private static IntPtr _uriClass;
        private static IntPtr _uriConstructor;
        private static IntPtr _tmpFindObjects;
        private static IntPtr _tmpGetText;
        private static IntPtr _tmpSetText;
        private static IntPtr _tmpTypeObject;
        private static IntPtr _uiTextGetText;
        private static IntPtr _uiTextSetText;
        private static IntPtr _uiTextTypeObject;
        private static IntPtr _byteClass;
        private static IntPtr _webRequestClass;
        private static IntPtr _webRequestGet;
        private static IntPtr _uploadHandlerRawClass;
        private static IntPtr _downloadHandlerBufferClass;
        private static IntPtr _webRequestConstructor;
        private static IntPtr _uploadHandlerRawConstructor;
        private static IntPtr _downloadHandlerBufferConstructor;
        private static IntPtr _webRequestSend;
        private static IntPtr _webRequestIsDone;
        private static IntPtr _webRequestResponseCode;
        private static IntPtr _webRequestDownloadHandler;
        private static IntPtr _webRequestSetHeader;
        private static IntPtr _webRequestAbort;
        private static IntPtr _webRequestDispose;
        private static IntPtr _downloadHandlerText;
        private static UnityUpdate _originalEventSystemUpdate;
        private static UnityUpdate _eventSystemUpdateDetour;
        private static IntPtr _eventSystemUpdateTargetSlot;
        private static int _hitLogged;
        private static int _detourErrorLogged;
        private static int _mosaicLogged;
        private static int _mosaicErrorLogged;
        private static int _fontRedirectLogged;
        private static int _fontRedirectErrorLogged;
        private static int _tmpScanHitLogged;
        private static int _tmpScanErrorLogged;
        private static int _dictionaryPrefetchErrorLogged;
        private static int _adultDictionaryMappingMissingLogged;
        private static int _llmAppliedLogged;
        private static RuntimeTranslator _runtimeTranslator;
        private static long _activeDictionaryRequestStarted;
        private static long _nextDictionaryRequest;
        private static uint _activeDictionaryRequestHandle;
        private static string _activeDictionaryKey;
        private static int _activeDictionaryAttempt;
        private static long _nextTmpScan;
        private static long _nextLlmRequest;
        private static long _activeRequestStarted;
        private static uint _activeRequestHandle;
        private static string _activeSource;
        private static int _activeAttempt;
        private static string _retrySource;
        private static int _retryAttempt;
        private static int _llmRequestLogged;
        private static bool _llmCircuitOpen;

        public override void OnPreInitialization()
        {
            try
            {
                var il2cpp = NativeLibrary.Load("libil2cpp.so");
                var domainGet = (DomainGet)il2cpp.GetExport(typeof(DomainGet), "il2cpp_domain_get");
                var assemblyOpen = (DomainAssemblyOpen)il2cpp.GetExport(
                    typeof(DomainAssemblyOpen), "il2cpp_domain_assembly_open");
                var assemblyGetImage = (AssemblyGetImage)il2cpp.GetExport(
                    typeof(AssemblyGetImage), "il2cpp_assembly_get_image");
                var classFromName = (ClassFromName)il2cpp.GetExport(
                    typeof(ClassFromName), "il2cpp_class_from_name");
                _classGetMethod = (ClassGetMethodFromName)il2cpp.GetExport(
                    typeof(ClassGetMethodFromName), "il2cpp_class_get_method_from_name");
                _classGetMethods = (ClassGetMethods)il2cpp.GetExport(
                    typeof(ClassGetMethods), "il2cpp_class_get_methods");
                _methodGetName = (MethodGetName)il2cpp.GetExport(
                    typeof(MethodGetName), "il2cpp_method_get_name");
                _methodGetParamCount = (MethodGetParamCount)il2cpp.GetExport(
                    typeof(MethodGetParamCount), "il2cpp_method_get_param_count");
                _methodGetParam = (MethodGetParam)il2cpp.GetExport(
                    typeof(MethodGetParam), "il2cpp_method_get_param");
                _typeGetName = (TypeGetName)il2cpp.GetExport(
                    typeof(TypeGetName), "il2cpp_type_get_name");
                _classGetType = (ClassGetType)il2cpp.GetExport(
                    typeof(ClassGetType), "il2cpp_class_get_type");
                _typeGetObject = (TypeGetObject)il2cpp.GetExport(
                    typeof(TypeGetObject), "il2cpp_type_get_object");
                _classGetField = (ClassGetFieldFromName)il2cpp.GetExport(
                    typeof(ClassGetFieldFromName), "il2cpp_class_get_field_from_name");
                _classGetParent = (ClassGetParent)il2cpp.GetExport(
                    typeof(ClassGetParent), "il2cpp_class_get_parent");
                _objectGetClass = (ObjectGetClass)il2cpp.GetExport(
                    typeof(ObjectGetClass), "il2cpp_object_get_class");
                _fieldGetValue = (FieldGetValue)il2cpp.GetExport(
                    typeof(FieldGetValue), "il2cpp_field_get_value");
                _runtimeInvoke = (RuntimeInvoke)il2cpp.GetExport(
                    typeof(RuntimeInvoke), "il2cpp_runtime_invoke");
                _stringLength = (StringLength)il2cpp.GetExport(
                    typeof(StringLength), "il2cpp_string_length");
                _stringChars = (StringChars)il2cpp.GetExport(
                    typeof(StringChars), "il2cpp_string_chars");
                _stringNewUtf16 = (StringNewUtf16)il2cpp.GetExport(
                    typeof(StringNewUtf16), "il2cpp_string_new_utf16");
                _arrayLength = (ArrayLength)il2cpp.GetExport(
                    typeof(ArrayLength), "il2cpp_array_length");
                _arrayNew = (ArrayNew)il2cpp.GetExport(
                    typeof(ArrayNew), "il2cpp_array_new");
                _objectUnbox = (ObjectUnbox)il2cpp.GetExport(
                    typeof(ObjectUnbox), "il2cpp_object_unbox");
                _gcHandleNew = (GcHandleNew)il2cpp.GetExport(
                    typeof(GcHandleNew), "il2cpp_gchandle_new");
                _gcHandleGetTarget = (GcHandleGetTarget)il2cpp.GetExport(
                    typeof(GcHandleGetTarget), "il2cpp_gchandle_get_target");
                _gcHandleFree = (GcHandleFree)il2cpp.GetExport(
                    typeof(GcHandleFree), "il2cpp_gchandle_free");
                var getCorlib = (GetCorlib)il2cpp.GetExport(
                    typeof(GetCorlib), "il2cpp_get_corlib");
                var corlib = getCorlib();
                _byteClass = classFromName(corlib, "System", "Byte");
                if (_byteClass == IntPtr.Zero)
                    throw new InvalidOperationException("System.Byte was not found");

                var assembly = assemblyOpen(domainGet(), "Assembly-CSharp-firstpass");
                if (assembly == IntPtr.Zero)
                    throw new InvalidOperationException("Assembly-CSharp-firstpass was not found");

                var klass = classFromName(assemblyGetImage(assembly), "BestHTTP", "HTTPResponse");
                if (klass == IntPtr.Zero)
                    throw new InvalidOperationException("BestHTTP.HTTPResponse was not found");

                var method = FindMethod(klass, "get_DataAsText");
                var target = method == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(method);
                if (target == IntPtr.Zero)
                    throw new InvalidOperationException("get_DataAsText native pointer was not found");

                _detour = GetDataAsText;
                _targetSlot = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_targetSlot, target);
                MelonUtils.NativeHookAttach(_targetSlot, Marshal.GetFunctionPointerForDelegate(_detour));
                _original = (DataAsTextGetter)Marshal.GetDelegateForFunctionPointer(
                    Marshal.ReadIntPtr(_targetSlot), typeof(DataAsTextGetter));

                MelonLogger.Msg("[OtogiTranslate] hook-installed");
                OtogiCgUnlock.OtogiCgUnlockRuntime.Install();

                try
                {
                    var unityAssembly = assemblyOpen(domainGet(), "UnityEngine.CoreModule");
                    var application = unityAssembly == IntPtr.Zero
                        ? IntPtr.Zero
                        : classFromName(
                            assemblyGetImage(unityAssembly), "UnityEngine", "Application");
                    var frameRateMethod = application == IntPtr.Zero
                        ? IntPtr.Zero
                        : _classGetMethod(application, "set_targetFrameRate", 1);
                    var frameRateTarget = frameRateMethod == IntPtr.Zero
                        ? IntPtr.Zero
                        : Marshal.ReadIntPtr(frameRateMethod);
                    if (frameRateTarget == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "Application.set_targetFrameRate native pointer was not found");

                    _frameRateDetour = SetTargetFrameRate;
                    _frameRateTargetSlot = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(_frameRateTargetSlot, frameRateTarget);
                    MelonUtils.NativeHookAttach(
                        _frameRateTargetSlot,
                        Marshal.GetFunctionPointerForDelegate(_frameRateDetour));
                    _originalFrameRate = (TargetFrameRateSetter)
                        Marshal.GetDelegateForFunctionPointer(
                            Marshal.ReadIntPtr(_frameRateTargetSlot),
                            typeof(TargetFrameRateSetter));
                    _originalFrameRate(60, frameRateMethod);
                    MelonLogger.Msg("[OtogiTranslate] framerate-installed fps=60");
                }
                catch (Exception exception)
                {
                    MelonLogger.Error(
                        "[OtogiTranslate] framerate-failed: " + exception.Message);
                }

                try
                {
                    var firstPassImage = assemblyGetImage(assembly);
                    var atlasAsset = classFromName(
                        firstPassImage, "Spine.Unity", "AtlasAsset");
                    var atlasMethod = atlasAsset == IntPtr.Zero
                        ? IntPtr.Zero
                        : FindMethod(atlasAsset, "GetAtlas");
                    var atlasTarget = atlasMethod == IntPtr.Zero
                        ? IntPtr.Zero
                        : Marshal.ReadIntPtr(atlasMethod);
                    if (atlasTarget == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "Spine.Unity.AtlasAsset.GetAtlas native pointer was not found");

                    var unityAssembly = assemblyOpen(domainGet(), "UnityEngine.CoreModule");
                    var unityImage = unityAssembly == IntPtr.Zero
                        ? IntPtr.Zero
                        : assemblyGetImage(unityAssembly);
                    var material = unityImage == IntPtr.Zero
                        ? IntPtr.Zero
                        : classFromName(unityImage, "UnityEngine", "Material");
                    var shader = unityImage == IntPtr.Zero
                        ? IntPtr.Zero
                        : classFromName(unityImage, "UnityEngine", "Shader");
                    if (material == IntPtr.Zero || shader == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "UnityEngine.Material or Shader was not found");

                    _materialGetShader = FindMethod(material, "get_shader");
                    _shaderGetName = FindMethod(shader, "get_name");
                    _materialSetFloat = FindMethod(
                        material, "SetFloat", 2, "System.String");

                    _atlasDetour = GetAtlas;
                    _atlasTargetSlot = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(_atlasTargetSlot, atlasTarget);
                    MelonUtils.NativeHookAttach(
                        _atlasTargetSlot,
                        Marshal.GetFunctionPointerForDelegate(_atlasDetour));
                    _originalAtlas = (AtlasGetter)Marshal.GetDelegateForFunctionPointer(
                        Marshal.ReadIntPtr(_atlasTargetSlot), typeof(AtlasGetter));
                    MelonLogger.Msg("[OtogiTranslate] mosaic-installed block-size=0.001");
                }
                catch (Exception exception)
                {
                    MelonLogger.Error(
                        "[OtogiTranslate] mosaic-failed: " + exception.Message);
                }

                try
                {
                    _objectNew = (ObjectNew)il2cpp.GetExport(
                        typeof(ObjectNew), "il2cpp_object_new");
                    var requestClass = classFromName(
                        assemblyGetImage(assembly), "BestHTTP", "HTTPRequest");
                    var requestConstructor = requestClass == IntPtr.Zero
                        ? IntPtr.Zero
                        : FindMethod(requestClass, ".ctor", 5, "System.Uri");
                    var requestConstructorTarget = requestConstructor == IntPtr.Zero
                        ? IntPtr.Zero
                        : Marshal.ReadIntPtr(requestConstructor);
                    if (requestConstructorTarget == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "BestHTTP.HTTPRequest constructor was not found");

                    var systemAssembly = assemblyOpen(domainGet(), "System");
                    _uriClass = systemAssembly == IntPtr.Zero
                        ? IntPtr.Zero
                        : classFromName(
                            assemblyGetImage(systemAssembly), "System", "Uri");
                    if (_uriClass == IntPtr.Zero)
                        throw new InvalidOperationException("System.Uri was not found");
                    _uriConstructor = FindMethod(
                        _uriClass, ".ctor", 1, "System.String");

                    _requestConstructorDetour = ConstructRequest;
                    _requestConstructorTargetSlot = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(
                        _requestConstructorTargetSlot, requestConstructorTarget);
                    MelonUtils.NativeHookAttach(
                        _requestConstructorTargetSlot,
                        Marshal.GetFunctionPointerForDelegate(
                            _requestConstructorDetour));
                    _originalRequestConstructor = (HttpRequestConstructor)
                        Marshal.GetDelegateForFunctionPointer(
                            Marshal.ReadIntPtr(_requestConstructorTargetSlot),
                            typeof(HttpRequestConstructor));
                    MelonLogger.Msg("[OtogiTranslate] font-redirect-installed");
                }
                catch (Exception exception)
                {
                    MelonLogger.Error(
                        "[OtogiTranslate] font-redirect-failed: " + exception.Message);
                }

                try
                {
                    _runtimeTranslator = new RuntimeTranslator(
                        MelonUtils.GameDirectory,
                        message => MelonLogger.Msg(message),
                        message => MelonLogger.Warning(message));
                    MelonLogger.Msg(string.Format(
                        "[OtogiTranslate] config-loaded path={0} llm={1}",
                        _runtimeTranslator.ConfigPath,
                        _runtimeTranslator.Config.Enable ? "enabled" : "disabled"));

                    var webAssembly = assemblyOpen(
                        domainGet(), "UnityEngine.UnityWebRequestModule");
                    if (webAssembly == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "UnityWebRequestModule assembly was not found");
                    var webImage = assemblyGetImage(webAssembly);
                    if (webImage == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "UnityWebRequestModule image was not found");
                    _webRequestClass = classFromName(
                        webImage, "UnityEngine.Networking", "UnityWebRequest");
                    _downloadHandlerBufferClass = classFromName(
                        webImage, "UnityEngine.Networking", "DownloadHandlerBuffer");
                    if (_webRequestClass == IntPtr.Zero ||
                        _downloadHandlerBufferClass == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "UnityWebRequest or DownloadHandlerBuffer was not found");

                    _webRequestGet = FindMethod(
                        _webRequestClass, "Get", 1, "System.String");
                    _webRequestSend = FindMethod(
                        _webRequestClass, "SendWebRequest");
                    _webRequestIsDone = FindMethod(
                        _webRequestClass, "get_isDone");
                    _webRequestResponseCode = FindMethod(
                        _webRequestClass, "get_responseCode");
                    _webRequestDownloadHandler = FindMethod(
                        _webRequestClass, "get_downloadHandler");
                    _webRequestAbort = FindMethod(_webRequestClass, "Abort");
                    _webRequestDispose = FindMethod(_webRequestClass, "Dispose");
                    _downloadHandlerText = FindMethod(
                        _downloadHandlerBufferClass, "get_text");
                    MelonLogger.Msg(
                        "[OtogiTranslate] dictionary-transport-installed UnityWebRequest");

                    if (_runtimeTranslator.Config.Enable)
                    {
                        _uploadHandlerRawClass = classFromName(
                            webImage, "UnityEngine.Networking", "UploadHandlerRaw");
                        if (_uploadHandlerRawClass == IntPtr.Zero)
                            throw new InvalidOperationException(
                                "UploadHandlerRaw was not found");
                        _webRequestConstructor = FindMethod(
                            _webRequestClass, ".ctor", 4, "System.String");
                        _uploadHandlerRawConstructor = FindMethod(
                            _uploadHandlerRawClass, ".ctor", 1, "System.Byte[]");
                        _downloadHandlerBufferConstructor = FindMethod(
                            _downloadHandlerBufferClass, ".ctor");
                        _webRequestSetHeader = FindMethod(
                            _webRequestClass, "SetRequestHeader", 2, "System.String");
                        MelonLogger.Msg(
                            "[OtogiTranslate] llm-transport-installed UnityWebRequest");
                    }

                    var uiAssembly = assemblyOpen(domainGet(), "UnityEngine.UI");
                    var eventSystem = uiAssembly == IntPtr.Zero
                        ? IntPtr.Zero
                        : classFromName(
                            assemblyGetImage(uiAssembly),
                            "UnityEngine.EventSystems", "EventSystem");
                    if (eventSystem == IntPtr.Zero)
                        throw new InvalidOperationException("EventSystem was not found");
                    var updateMethod = FindMethod(eventSystem, "Update");
                    var updateTarget = Marshal.ReadIntPtr(updateMethod);
                    if (updateTarget == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "EventSystem.Update native pointer was not found");
                    _eventSystemUpdateDetour = EventSystemUpdate;
                    _eventSystemUpdateTargetSlot = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(_eventSystemUpdateTargetSlot, updateTarget);
                    MelonUtils.NativeHookAttach(
                        _eventSystemUpdateTargetSlot,
                        Marshal.GetFunctionPointerForDelegate(_eventSystemUpdateDetour));
                    _originalEventSystemUpdate = (UnityUpdate)
                        Marshal.GetDelegateForFunctionPointer(
                            Marshal.ReadIntPtr(_eventSystemUpdateTargetSlot),
                            typeof(UnityUpdate));
                    MelonLogger.Msg(
                        "[OtogiTranslate] runtime-driver-installed EventSystem.Update");

                    if (_runtimeTranslator.ShouldScan)
                    {
                        var tmpAssembly = assemblyOpen(domainGet(), "Unity.TextMeshPro");
                        var unityAssembly = assemblyOpen(domainGet(), "UnityEngine.CoreModule");
                        var tmpClass = tmpAssembly == IntPtr.Zero
                            ? IntPtr.Zero
                            : classFromName(
                                assemblyGetImage(tmpAssembly), "TMPro", "TMP_Text");
                        var unityObject = unityAssembly == IntPtr.Zero
                            ? IntPtr.Zero
                            : classFromName(
                                assemblyGetImage(unityAssembly), "UnityEngine", "Object");
                        var uiTextClass = uiAssembly == IntPtr.Zero
                            ? IntPtr.Zero
                            : classFromName(
                                assemblyGetImage(uiAssembly),
                                "UnityEngine.UI", "Text");
                        if (tmpClass == IntPtr.Zero || unityObject == IntPtr.Zero ||
                            uiTextClass == IntPtr.Zero)
                            throw new InvalidOperationException(
                                "TMP_Text, UI.Text, or UnityEngine.Object was not found");

                        _tmpFindObjects = FindMethod(
                            unityObject, "FindObjectsOfType", 1, "System.Type");
                        _tmpGetText = FindMethod(tmpClass, "get_text");
                        _tmpSetText = FindMethod(
                            tmpClass, "set_text", 1, "System.String");
                        _tmpTypeObject = _typeGetObject(_classGetType(tmpClass));
                        _uiTextGetText = FindMethod(uiTextClass, "get_text");
                        _uiTextSetText = FindMethod(
                            uiTextClass, "set_text", 1, "System.String");
                        _uiTextTypeObject = _typeGetObject(_classGetType(uiTextClass));
                        if (_tmpTypeObject == IntPtr.Zero || _uiTextTypeObject == IntPtr.Zero)
                            throw new InvalidOperationException(
                                "TMP_Text or UI.Text System.Type was not found");
                        MelonLogger.Msg(string.Format(
                            CultureInfo.InvariantCulture,
                            "[OtogiTranslate] ui-scan-installed interval={0:0.###}s driver=EventSystem.Update",
                            _runtimeTranslator.Config.ScanIntervalSeconds));
                    }
                    else
                    {
                        MelonLogger.Msg("[OtogiTranslate] tmp-scan-disabled");
                    }
                }
                catch (Exception exception)
                {
                    if (_runtimeTranslator != null)
                    {
                        _runtimeTranslator.Dispose();
                        _runtimeTranslator = null;
                    }
                    MelonLogger.Error(
                        "[OtogiTranslate] runtime-init-failed: " + exception.Message);
                }
            }
            catch (Exception exception)
            {
                MelonLogger.Error("[OtogiTranslate] hook-failed: " + exception.Message);
            }
        }

        public override void OnUpdate()
        {
            TickRuntime();
        }

        private static void EventSystemUpdate(IntPtr instance, IntPtr methodInfo)
        {
            _originalEventSystemUpdate(instance, methodInfo);
            TickRuntime();
        }

        private static void TickRuntime()
        {
            TickDictionaryQueue();
            TickLlmQueue();
            TickTmpScan();
            OtogiCgUnlock.OtogiCgUnlockRuntime.Tick();
        }

        private static void TickDictionaryQueue()
        {
            if (_webRequestGet == IntPtr.Zero)
                return;

            if (_activeDictionaryRequestHandle != 0)
            {
                PollDictionaryRequest();
                if (_activeDictionaryRequestHandle != 0)
                    return;
            }

            if (Stopwatch.GetTimestamp() < _nextDictionaryRequest)
                return;

            string key;
            lock (CacheLock)
            {
                if (DictionaryDownloads.Count == 0)
                    return;
                key = DictionaryDownloads.Dequeue();
            }
            StartDictionaryRequest(key);
        }

        private static void StartDictionaryRequest(string key)
        {
            _activeDictionaryKey = key;
            lock (CacheLock)
            {
                int attempt;
                DictionaryAttempts.TryGetValue(key, out attempt);
                _activeDictionaryAttempt = attempt + 1;
                DictionaryAttempts[key] = _activeDictionaryAttempt;
            }
            try
            {
                var request = Invoke(
                    _webRequestGet,
                    IntPtr.Zero,
                    ToIl2CppString(RemoteRoot + "/" + key + "_gb.json"));
                _activeDictionaryRequestHandle = _gcHandleNew(request, false);
                if (_activeDictionaryRequestHandle == 0)
                    throw new InvalidOperationException("Could not retain dictionary request");
                _activeDictionaryRequestStarted = Stopwatch.GetTimestamp();
                Invoke(_webRequestSend, request);
                MelonLogger.Msg("[OtogiTranslate] dictionary-request-started key=" + key);
            }
            catch (Exception exception)
            {
                FailDictionaryRequest("request-start-" + exception.GetType().Name);
            }
        }

        private static void PollDictionaryRequest()
        {
            var request = _gcHandleGetTarget(_activeDictionaryRequestHandle);
            if (request == IntPtr.Zero)
            {
                FailDictionaryRequest("request-lost");
                return;
            }
            if (Stopwatch.GetTimestamp() - _activeDictionaryRequestStarted >
                30L * Stopwatch.Frequency)
            {
                FailDictionaryRequest("timeout");
                return;
            }

            try
            {
                if (!ReadBoxedBoolean(Invoke(_webRequestIsDone, request)))
                    return;
                var status = ReadBoxedInt64(Invoke(_webRequestResponseCode, request));
                if (status < 200 || status >= 300)
                {
                    FailDictionaryRequest(status == 0
                        ? "unitywebrequest-error"
                        : "http-" + status.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                var download = Invoke(_webRequestDownloadHandler, request);
                var json = ToManagedString(Invoke(_downloadHandlerText, download));
                if (json == null || json.Length > 4 * 1024 * 1024)
                    throw new InvalidDataException("translation JSON is too large");
                var dictionary = TranslationLogic.ParseDictionary(json);
                var key = _activeDictionaryKey;
                var slash = key.IndexOf('/');
                var path = GetDictionaryPath(
                    key.Substring(0, slash), key.Substring(slash + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporaryPath = path + ".tmp";
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temporaryPath, path);
                ReleaseDictionaryRequest(false);
                lock (CacheLock)
                {
                    Cache[key] = dictionary;
                    PendingDictionaries.Remove(key);
                    DictionaryAttempts.Remove(key);
                }
                MelonLogger.Msg(string.Format(
                    CultureInfo.InvariantCulture,
                    "[OtogiTranslate] dictionary-downloaded type={0} id={1} entries={2}",
                    key.Substring(0, slash),
                    key.Substring(slash + 1),
                    dictionary.Count));
            }
            catch (Exception exception)
            {
                FailDictionaryRequest("response-" + exception.GetType().Name);
            }
        }

        private static void FailDictionaryRequest(string reason)
        {
            var key = _activeDictionaryKey;
            var attempt = _activeDictionaryAttempt;
            ReleaseDictionaryRequest(true);
            if (key == null)
                return;
            if (IsRetryableRequestFailure(reason) && attempt < 3)
            {
                lock (CacheLock)
                    DictionaryDownloads.Enqueue(key);
                _nextDictionaryRequest = Stopwatch.GetTimestamp() +
                    (1L << (attempt - 1)) * Stopwatch.Frequency;
                MelonLogger.Warning(string.Format(
                    CultureInfo.InvariantCulture,
                    "[OtogiTranslate] dictionary-request-retry key={0} attempt={1}",
                    key,
                    attempt + 1));
                return;
            }
            lock (CacheLock)
            {
                PendingDictionaries.Remove(key);
                DictionaryAttempts.Remove(key);
                if (string.Equals(reason, "http-404", StringComparison.Ordinal))
                    UnavailableDictionaries.Add(key);
            }
            MelonLogger.Warning(string.Format(
                CultureInfo.InvariantCulture,
                "[OtogiTranslate] dictionary-download-failed key={0} reason={1}",
                key,
                reason));
        }

        private static void ReleaseDictionaryRequest(bool abort)
        {
            var handle = _activeDictionaryRequestHandle;
            _activeDictionaryRequestHandle = 0;
            _activeDictionaryRequestStarted = 0;
            _activeDictionaryKey = null;
            _activeDictionaryAttempt = 0;
            if (handle == 0)
                return;
            try
            {
                var request = _gcHandleGetTarget(handle);
                if (request != IntPtr.Zero)
                {
                    if (abort)
                        Invoke(_webRequestAbort, request);
                    Invoke(_webRequestDispose, request);
                }
            }
            catch
            {
                // Releasing a completed/aborted request is best-effort during teardown.
            }
            finally
            {
                _gcHandleFree(handle);
            }
        }

        private static void TickLlmQueue()
        {
            if (_runtimeTranslator == null || !_runtimeTranslator.Config.Enable ||
                _webRequestClass == IntPtr.Zero || _llmCircuitOpen)
                return;

            if (_activeRequestHandle != 0)
            {
                PollLlmRequest();
                if (_activeRequestHandle != 0)
                    return;
            }

            var now = Stopwatch.GetTimestamp();
            if (now < _nextLlmRequest)
                return;

            string source;
            int attempt;
            if (_retrySource != null)
            {
                source = _retrySource;
                attempt = _retryAttempt;
                _retrySource = null;
                _retryAttempt = 0;
            }
            else
            {
                if (!_runtimeTranslator.TryTake(out source))
                    return;
                attempt = 1;
            }
            StartLlmRequest(source, attempt);
        }

        private static void StartLlmRequest(string source, int attempt)
        {
            _activeSource = source;
            _activeAttempt = attempt;
            try
            {
                var payload = _runtimeTranslator.BuildRequestBody(source);
                var bytes = _arrayNew(_byteClass, new UIntPtr((uint)payload.Length));
                if (bytes == IntPtr.Zero)
                    throw new InvalidOperationException("Could not allocate request body");
                // ponytail: Unity 2022.3 IL2CPP array layout; use generated wrappers if it changes.
                Marshal.Copy(payload, 0, IntPtr.Add(bytes, 4 * IntPtr.Size), payload.Length);

                var upload = _objectNew(_uploadHandlerRawClass);
                Invoke(_uploadHandlerRawConstructor, upload, bytes);
                var download = _objectNew(_downloadHandlerBufferClass);
                Invoke(_downloadHandlerBufferConstructor, download);
                var request = _objectNew(_webRequestClass);
                Invoke(
                    _webRequestConstructor,
                    request,
                    ToIl2CppString(_runtimeTranslator.Config.Endpoint),
                    ToIl2CppString("POST"),
                    download,
                    upload);
                Invoke(
                    _webRequestSetHeader,
                    request,
                    ToIl2CppString("Content-Type"),
                    ToIl2CppString("application/json"));
                Invoke(
                    _webRequestSetHeader,
                    request,
                    ToIl2CppString("Accept"),
                    ToIl2CppString("application/json"));
                if (!string.IsNullOrWhiteSpace(_runtimeTranslator.Config.ApiKey))
                {
                    Invoke(
                        _webRequestSetHeader,
                        request,
                        ToIl2CppString("Authorization"),
                        ToIl2CppString("Bearer " + _runtimeTranslator.Config.ApiKey));
                }

                _activeRequestHandle = _gcHandleNew(request, false);
                if (_activeRequestHandle == 0)
                    throw new InvalidOperationException("Could not retain UnityWebRequest");
                _activeRequestStarted = Stopwatch.GetTimestamp();
                Invoke(_webRequestSend, request);
                if (Interlocked.Exchange(ref _llmRequestLogged, 1) == 0)
                    MelonLogger.Msg(
                        "[OtogiTranslate] llm-request-started transport=UnityWebRequest");
            }
            catch (Exception exception)
            {
                FailActiveRequest("request-start-" + exception.GetType().Name);
            }
        }

        private static void PollLlmRequest()
        {
            var request = _gcHandleGetTarget(_activeRequestHandle);
            if (request == IntPtr.Zero)
            {
                FailActiveRequest("request-lost");
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (now - _activeRequestStarted >
                (long)_runtimeTranslator.Config.TimeoutSeconds * Stopwatch.Frequency)
            {
                FailActiveRequest("timeout");
                return;
            }

            try
            {
                if (!ReadBoxedBoolean(Invoke(_webRequestIsDone, request)))
                    return;

                var status = ReadBoxedInt64(Invoke(_webRequestResponseCode, request));
                if (status < 200 || status >= 300)
                {
                    FailActiveRequest(status == 0
                        ? "unitywebrequest-error"
                        : "http-" + status.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                var download = Invoke(_webRequestDownloadHandler, request);
                var json = ToManagedString(Invoke(_downloadHandlerText, download));
                var source = _activeSource;
                ReleaseActiveRequest(false);
                _runtimeTranslator.Accept(source, json);
                DelayNextRequest(1000 / _runtimeTranslator.Config.RequestsPerSecond);
            }
            catch (Exception exception)
            {
                FailActiveRequest("request-poll-" + exception.GetType().Name);
            }
        }

        private static void FailActiveRequest(string reason)
        {
            var source = _activeSource;
            var attempt = _activeAttempt;
            ReleaseActiveRequest(true);
            var retryable = IsRetryableRequestFailure(reason);
            if (_runtimeTranslator != null && source != null && retryable &&
                attempt <= _runtimeTranslator.Config.RetryCount)
            {
                _retrySource = source;
                _retryAttempt = attempt + 1;
                DelayNextRequest(Math.Min(30000, 1000 << Math.Min(attempt - 1, 4)));
                return;
            }
            if (_runtimeTranslator != null && source != null)
                _runtimeTranslator.Fail(source, reason);
            if (_runtimeTranslator == null)
                return;
            if (IsPermanentLlmFailure(reason))
            {
                _llmCircuitOpen = true;
                MelonLogger.Warning("[OtogiTranslate] llm-paused reason=" + reason);
            }
            else
            {
                DelayNextRequest(retryable
                    ? 30000
                    : 1000 / _runtimeTranslator.Config.RequestsPerSecond);
            }
        }

        private static bool IsRetryableRequestFailure(string reason)
        {
            int status;
            if (!reason.StartsWith("http-", StringComparison.Ordinal) ||
                !int.TryParse(reason.Substring(5), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out status))
                return true;
            return status == 408 || status == 429 || status >= 500;
        }

        private static bool IsPermanentLlmFailure(string reason)
        {
            int status;
            return reason.StartsWith("http-", StringComparison.Ordinal) &&
                int.TryParse(reason.Substring(5), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out status) &&
                status >= 400 && status < 500 && status != 408 && status != 429;
        }

        private static void ReleaseActiveRequest(bool abort)
        {
            var handle = _activeRequestHandle;
            _activeRequestHandle = 0;
            _activeRequestStarted = 0;
            _activeSource = null;
            _activeAttempt = 0;
            if (handle == 0)
                return;

            try
            {
                var request = _gcHandleGetTarget(handle);
                if (request != IntPtr.Zero)
                {
                    if (abort)
                        Invoke(_webRequestAbort, request);
                    Invoke(_webRequestDispose, request);
                }
            }
            catch
            {
                // Releasing a completed/aborted request is best-effort during teardown.
            }
            finally
            {
                _gcHandleFree(handle);
            }
        }

        private static void DelayNextRequest(int milliseconds)
        {
            _nextLlmRequest = Stopwatch.GetTimestamp() +
                (long)milliseconds * Stopwatch.Frequency / 1000;
        }

        private static bool ReadBoxedBoolean(IntPtr value)
        {
            var data = value == IntPtr.Zero ? IntPtr.Zero : _objectUnbox(value);
            if (data == IntPtr.Zero)
                throw new InvalidOperationException("Expected a boxed Boolean");
            return Marshal.ReadByte(data) != 0;
        }

        private static long ReadBoxedInt64(IntPtr value)
        {
            var data = value == IntPtr.Zero ? IntPtr.Zero : _objectUnbox(value);
            if (data == IntPtr.Zero)
                throw new InvalidOperationException("Expected a boxed Int64");
            return Marshal.ReadInt64(data);
        }

        private static void TickTmpScan()
        {
            if (_runtimeTranslator == null || !_runtimeTranslator.ShouldScan ||
                _tmpFindObjects == IntPtr.Zero)
                return;

            var now = Stopwatch.GetTimestamp();
            if (now < _nextTmpScan)
                return;
            _nextTmpScan = now + (long)(
                _runtimeTranslator.Config.ScanIntervalSeconds * Stopwatch.Frequency);
            ScanTmpText();
        }

        public override void OnApplicationQuit()
        {
            ReleaseDictionaryRequest(true);
            ReleaseActiveRequest(true);
            var translator = _runtimeTranslator;
            _runtimeTranslator = null;
            if (translator != null)
                translator.Dispose();
        }

        private static void SetTargetFrameRate(int value, IntPtr methodInfo)
        {
            _originalFrameRate(60, methodInfo);
        }

        private static IntPtr GetAtlas(IntPtr instance, IntPtr methodInfo)
        {
            try
            {
                var materials = ReadReferenceField(instance, "materials");
                if (materials != IntPtr.Zero && _arrayLength(materials).ToUInt64() != 0)
                {
                    // ponytail: Unity 2022.3 IL2CPP layout; use generated wrappers if the engine changes.
                    var material = Marshal.ReadIntPtr(materials, 4 * IntPtr.Size);
                    var shader = material == IntPtr.Zero
                        ? IntPtr.Zero
                        : Invoke(_materialGetShader, material);
                    var shaderName = shader == IntPtr.Zero
                        ? null
                        : ToManagedString(Invoke(_shaderGetName, shader));
                    if (string.Equals(
                        shaderName, "Spine/SkeletonMosaic", StringComparison.Ordinal))
                    {
                        var value = Marshal.AllocHGlobal(4);
                        try
                        {
                            Marshal.Copy(BitConverter.GetBytes(0.001f), 0, value, 4);
                            Invoke(
                                _materialSetFloat,
                                material,
                                ToIl2CppString("_BlockSize"),
                                value);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(value);
                        }
                        if (Interlocked.Exchange(ref _mosaicLogged, 1) == 0)
                            MelonLogger.Msg(
                                "[OtogiTranslate] mosaic-patched block-size=0.001");
                    }
                }
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _mosaicErrorLogged, 1) == 0)
                    MelonLogger.Error(
                        "[OtogiTranslate] mosaic-error: " + exception.Message);
            }
            return _originalAtlas(instance, methodInfo);
        }

        private static void ConstructRequest(
            IntPtr instance,
            IntPtr uri,
            IntPtr argument1,
            IntPtr argument2,
            IntPtr argument3,
            IntPtr argument4,
            IntPtr methodInfo)
        {
            try
            {
                var absoluteUri = uri == IntPtr.Zero
                    ? IntPtr.Zero
                    : Invoke(FindMethod(_objectGetClass(uri), "get_AbsoluteUri"), uri);
                var requestUrl = ToManagedString(absoluteUri);
                if (TranslationLogic.IsFontUrl(requestUrl))
                {
                    var replacement = _objectNew(_uriClass);
                    Invoke(
                        _uriConstructor,
                        replacement,
                        ToIl2CppString(FontUrl));
                    uri = replacement;
                    requestUrl = FontUrl;
                    if (Interlocked.Exchange(ref _fontRedirectLogged, 1) == 0)
                        MelonLogger.Msg("[OtogiTranslate] font-redirected");
                }
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _fontRedirectErrorLogged, 1) == 0)
                    MelonLogger.Error(
                        "[OtogiTranslate] font-redirect-error: " + exception.Message);
            }
            _originalRequestConstructor(
                instance,
                uri,
                argument1,
                argument2,
                argument3,
                argument4,
                methodInfo);
        }

        private static void ScanTmpText()
        {
            try
            {
                var tmpCount = ScanTextType(
                    "tmp", _tmpTypeObject, _tmpGetText, _tmpSetText);
                var uiCount = ScanTextType(
                    "legacy", _uiTextTypeObject, _uiTextGetText, _uiTextSetText);
                if (Interlocked.Exchange(ref _tmpScanHitLogged, 1) == 0)
                    MelonLogger.Msg(string.Format(
                        "[OtogiTranslate] ui-scan-hit tmp={0} legacy={1}",
                        tmpCount, uiCount));
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _tmpScanErrorLogged, 1) == 0)
                    MelonLogger.Error(
                        "[OtogiTranslate] ui-scan-error: " + exception.Message);
            }
        }

        private static ulong ScanTextType(
            string component,
            IntPtr typeObject,
            IntPtr getText,
            IntPtr setText)
        {
            var array = Invoke(_tmpFindObjects, IntPtr.Zero, typeObject);
            var length = array == IntPtr.Zero
                ? 0UL
                : _arrayLength(array).ToUInt64();
            if (length > 10000)
                throw new InvalidOperationException("TMP_Text array was unexpectedly large");

            for (ulong index = 0; index < length; index++)
            {
                // ponytail: Unity 2022.3 IL2CPP array layout; use generated wrappers if it changes.
                var instance = Marshal.ReadIntPtr(
                    array, checked(4 * IntPtr.Size + (int)index * IntPtr.Size));
                if (instance == IntPtr.Zero)
                    continue;
                var source = ToManagedString(Invoke(getText, instance));
                var translated = _runtimeTranslator.Observe(source);
                if (string.Equals(source, translated, StringComparison.Ordinal))
                    continue;
                Invoke(setText, instance, ToIl2CppString(translated));
                var applied = Interlocked.Increment(ref _llmAppliedLogged);
                if (applied <= 64)
                    MelonLogger.Msg(string.Format(
                        CultureInfo.InvariantCulture,
                        "[OtogiTranslate] ui-translation-applied n={0} component={1}",
                        applied,
                        component));
            }
            return length;
        }

        private static IntPtr GetDataAsText(IntPtr instance, IntPtr methodInfo)
        {
            var result = _original(instance, methodInfo);
            if (Interlocked.Exchange(ref _hitLogged, 1) == 0)
                MelonLogger.Msg("[OtogiTranslate] getter-hit");
            if (result == IntPtr.Zero)
                return result;

            try
            {
                var responseUrl = GetResponseUrl(instance);
                var originalJson = ToManagedString(result);
                string rewritten;
                var originalStatus = OtogiCgUnlock.OtogiCgUnlockRuntime.OriginalHttpStatus(
                    instance, responseUrl);
                if (OtogiCgUnlock.OtogiCgUnlockRuntime.TryRewrite(
                    responseUrl, originalJson, originalStatus, out rewritten))
                {
                    originalJson = rewritten;
                    result = ToIl2CppString(rewritten);
                    if (originalStatus == 400 || originalStatus == 404)
                        Marshal.WriteInt32(instance, 0x18, 200);
                }
                QueueEpisodeDictionaries(responseUrl, originalJson);
                string type;
                string id;
                if (!TryGetTranslationKey(responseUrl, out type, out id))
                    return result;

                var dictionaryId = type == "MAdults"
                    ? GetAdultDictionaryId(id)
                    : id;
                if (dictionaryId == null)
                {
                    if (Interlocked.Exchange(
                        ref _adultDictionaryMappingMissingLogged, 1) == 0)
                    {
                        MelonLogger.Warning(
                            "[OtogiTranslate] adult-dictionary-mapping-missing id=" + id);
                    }
                    return result;
                }
                var dictionary = GetDictionary(type, dictionaryId);
                if (dictionary == null)
                    return result;

                int replacements;
                var translated = TranslationLogic.TranslateJson(
                    originalJson, dictionary, out replacements);
                if (replacements == 0)
                    return result;

                MelonLogger.Msg(string.Format(
                    "[OtogiTranslate] translated type={0} id={1} dictionary={2} replacements={3}",
                    type, id, dictionaryId, replacements));
                return ToIl2CppString(translated);
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _detourErrorLogged, 1) == 0)
                    MelonLogger.Error("[OtogiTranslate] response-error: " + exception.Message);
                return result;
            }
        }

        private static string GetResponseUrl(IntPtr response)
        {
            var request = ReadReferenceField(response, "baseRequest");
            if (request == IntPtr.Zero)
                throw new InvalidOperationException("HTTPResponse.baseRequest was null");

            var uri = Invoke(FindMethod(_objectGetClass(request), "get_Uri"), request);
            if (uri == IntPtr.Zero)
                throw new InvalidOperationException("HTTPRequest.Uri was null");

            var absoluteUri = Invoke(FindMethod(_objectGetClass(uri), "get_AbsoluteUri"), uri);
            return ToManagedString(absoluteUri);
        }

        private static bool TryGetTranslationKey(
            string url,
            out string type,
            out string id)
        {
            type = null;
            id = null;
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return false;

            var path = uri.AbsolutePath.TrimEnd('/');
            if (TryMatch(path, "/api/MAdults/MonsterMAdults/", out id))
                type = "MAdults";
            else if (TryMatch(path, "/api/MScenes/", out id))
                type = "MScenes";
            else if (TryMatch(path, "/api/Episode/MStory/", out id))
                type = "Mstory";
            else
                return false;
            return true;
        }

        private static bool TryMatch(string path, string marker, out string id)
        {
            id = null;
            var markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return false;

            var candidate = path.Substring(markerIndex + marker.Length);
            if (candidate.Length == 0 || candidate.Length > 10 || candidate.IndexOf('/') >= 0)
                return false;
            for (var index = 0; index < candidate.Length; index++)
            {
                if (candidate[index] < '0' || candidate[index] > '9')
                    return false;
            }
            id = candidate;
            return true;
        }

        private static void QueueEpisodeDictionaries(string url, string json)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(json))
                return;
            try
            {
                if (url.IndexOf("/api/episode/monsters/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    url.IndexOf("/api/episode/spirits/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var episodes = JObject.Parse(json)["Episodes"] as JArray;
                    if (episodes == null)
                        return;
                    foreach (var episode in episodes)
                    {
                        var sceneId = ReadNumericId(episode["MSceneId"]);
                        var adultId = ReadNumericId(episode["MAdultId"]);
                        if (sceneId != null && adultId != null)
                        {
                            lock (CacheLock)
                                AdultDictionaryIds[adultId] = sceneId;
                        }
                        if (episode.Value<bool?>("Viewable") != true)
                            continue;
                        QueueDictionary("MScenes", sceneId);
                        QueueDictionary("MAdults", sceneId);
                    }
                    return;
                }

                var worldStories = url.IndexOf(
                    "/api/Episode/WorldStories", StringComparison.OrdinalIgnoreCase) >= 0;
                var sideStories = url.IndexOf(
                    "/api/UAdventures/SideStories", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!worldStories && !sideStories)
                    return;
                var items = JArray.Parse(json);
                if (worldStories)
                {
                    foreach (var item in items)
                        QueueDictionary("Mstory", ReadNumericId(item["MStoryId"]));
                }
                else
                {
                    foreach (var item in items)
                    {
                        var adventures = item["Adventures"] as JArray;
                        if (adventures == null)
                            continue;
                        foreach (var adventure in adventures)
                            QueueDictionary("MScenes", ReadNumericId(adventure["MSceneId"]));
                    }
                }
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _dictionaryPrefetchErrorLogged, 1) == 0)
                    MelonLogger.Warning(
                        "[OtogiTranslate] dictionary-prefetch-error: " + exception.Message);
            }
        }

        private static string ReadNumericId(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            var value = token.Type == JTokenType.Integer
                ? Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture)
                : (string)token;
            if (string.IsNullOrEmpty(value) || value.Length > 10)
                return null;
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9')
                    return null;
            }
            return value;
        }

        private static string GetAdultDictionaryId(string adultId)
        {
            lock (CacheLock)
            {
                string sceneId;
                return AdultDictionaryIds.TryGetValue(adultId, out sceneId)
                    ? sceneId
                    : null;
            }
        }

        private static void QueueDictionary(string type, string id)
        {
            if (id == null)
                return;
            var key = type + "/" + id;
            if (File.Exists(GetDictionaryPath(type, id)))
                return;
            lock (CacheLock)
            {
                if (Cache.ContainsKey(key) || PendingDictionaries.Contains(key) ||
                    UnavailableDictionaries.Contains(key))
                    return;
                PendingDictionaries.Add(key);
                DictionaryDownloads.Enqueue(key);
            }
            MelonLogger.Msg("[OtogiTranslate] dictionary-queued key=" + key);
        }

        private static string GetDictionaryPath(string type, string id)
        {
            return Path.Combine(
                MelonUtils.GetApplicationPath(),
                "UserData", "OtogiTranslate", type, id + "_gb.json");
        }

        private static Dictionary<string, string> GetDictionary(string type, string id)
        {
            var key = type + "/" + id;
            lock (CacheLock)
            {
                Dictionary<string, string> cached;
                if (Cache.TryGetValue(key, out cached))
                    return cached;
            }

            var localPath = GetDictionaryPath(type, id);
            if (!File.Exists(localPath))
            {
                QueueDictionary(type, id);
                return null;
            }

            try
            {
                var cached = TranslationLogic.ParseDictionary(
                    File.ReadAllText(localPath, Encoding.UTF8));
                lock (CacheLock)
                    Cache[key] = cached;
                MelonLogger.Msg(string.Format(
                    CultureInfo.InvariantCulture,
                    "[OtogiTranslate] dictionary-loaded type={0} id={1} source=local entries={2}",
                    type, id, cached.Count));
                return cached;
            }
            catch (Exception exception)
            {
                Exception quarantineError = null;
                try
                {
                    var invalidPath = localPath + ".invalid";
                    if (File.Exists(invalidPath))
                        File.Delete(invalidPath);
                    File.Move(localPath, invalidPath);
                }
                catch (Exception quarantineException)
                {
                    quarantineError = quarantineException;
                }
                if (quarantineError == null)
                {
                    QueueDictionary(type, id);
                }
                else
                {
                    MelonLogger.Warning(string.Format(
                        CultureInfo.InvariantCulture,
                        "[OtogiTranslate] dictionary-quarantine-failed type={0} id={1}: {2}",
                        type, id, quarantineError.Message));
                }
                MelonLogger.Warning(string.Format(
                    CultureInfo.InvariantCulture,
                    "[OtogiTranslate] dictionary-invalid type={0} id={1}: {2}",
                    type, id, exception.Message));
                return null;
            }
        }

        private static IntPtr ReadReferenceField(IntPtr instance, string name)
        {
            var klass = _objectGetClass(instance);
            IntPtr field = IntPtr.Zero;
            while (klass != IntPtr.Zero && field == IntPtr.Zero)
            {
                field = _classGetField(klass, name);
                klass = _classGetParent(klass);
            }
            if (field == IntPtr.Zero)
                throw new MissingFieldException(name);

            var output = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(output, IntPtr.Zero);
                _fieldGetValue(instance, field, output);
                return Marshal.ReadIntPtr(output);
            }
            finally
            {
                Marshal.FreeHGlobal(output);
            }
        }

        private static IntPtr FindMethod(IntPtr klass, string name)
        {
            while (klass != IntPtr.Zero)
            {
                var method = _classGetMethod(klass, name, 0);
                if (method != IntPtr.Zero)
                    return method;
                klass = _classGetParent(klass);
            }
            throw new MissingMethodException(name);
        }

        private static IntPtr FindMethod(
            IntPtr klass,
            string name,
            int argumentCount,
            string firstParameterType)
        {
            while (klass != IntPtr.Zero)
            {
                var iterator = IntPtr.Zero;
                IntPtr method;
                while ((method = _classGetMethods(klass, ref iterator)) != IntPtr.Zero)
                {
                    if (_methodGetParamCount(method) != argumentCount ||
                        !string.Equals(
                            Marshal.PtrToStringAnsi(_methodGetName(method)),
                            name,
                            StringComparison.Ordinal))
                        continue;

                    var parameterType = argumentCount == 0
                        ? null
                        : Marshal.PtrToStringAnsi(
                            _typeGetName(_methodGetParam(method, 0)));
                    if (string.Equals(
                        parameterType, firstParameterType, StringComparison.Ordinal))
                        return method;
                }
                klass = _classGetParent(klass);
            }
            throw new MissingMethodException(name + "(" + firstParameterType + ")");
        }

        private static IntPtr Invoke(
            IntPtr method,
            IntPtr instance,
            params IntPtr[] arguments)
        {
            var parameters = IntPtr.Zero;
            try
            {
                if (arguments != null && arguments.Length != 0)
                {
                    parameters = Marshal.AllocHGlobal(IntPtr.Size * arguments.Length);
                    for (var index = 0; index < arguments.Length; index++)
                        Marshal.WriteIntPtr(
                            parameters, index * IntPtr.Size, arguments[index]);
                }

                var exception = IntPtr.Zero;
                var result = _runtimeInvoke(method, instance, parameters, ref exception);
                if (exception != IntPtr.Zero)
                    throw new InvalidOperationException("IL2CPP invocation failed");
                return result;
            }
            finally
            {
                if (parameters != IntPtr.Zero)
                    Marshal.FreeHGlobal(parameters);
            }
        }

        private static string ToManagedString(IntPtr value)
        {
            if (value == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringUni(_stringChars(value), _stringLength(value));
        }

        private static IntPtr ToIl2CppString(string value)
        {
            var chars = Marshal.StringToHGlobalUni(value);
            try
            {
                return _stringNewUtf16(chars, value.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(chars);
            }
        }
    }
#endif
}
