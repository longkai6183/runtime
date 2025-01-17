// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;

namespace System.Configuration
{
    internal sealed class ClientConfigPaths
    {
        internal const string UserConfigFilename = "user.config";

        private const string ConfigExtension = ".config";
        private const int MaxLengthToUse = 25;
        private const string HttpUri = "http://";

        private const string StrongNameDesc = "StrongName";
        private const string UrlDesc = "Url";
        private const string PathDesc = "Path";

        private static volatile ClientConfigPaths s_current;
        private static volatile bool s_currentIncludesUserConfig;

        private readonly bool _includesUserConfig;
        private string _companyName;

        [UnconditionalSuppressMessage("SingleFile", "IL3000: Avoid accessing Assembly file path when publishing as a single file",
            Justification = "Code handles single file case")]
        [UnconditionalSuppressMessage("SingleFile", "IL3002: RequiresAssemblyFiles on Module.Name",
            Justification = "Code handles single file case")]
        private ClientConfigPaths(string exePath, bool includeUserConfig)
        {
            _includesUserConfig = includeUserConfig;

            Assembly exeAssembly = null;
            bool isSingleFile = false;

            if (exePath != null)
            {
                // Exe path was specified, use it
                ApplicationUri = Path.GetFullPath(exePath);
                if (!File.Exists(ApplicationUri))
                {
                    throw ExceptionUtil.ParameterInvalid(nameof(exePath));
                }
            }
            else
            {
                // Exe path wasn't specified, get it from the entry assembly
                exeAssembly = Assembly.GetEntryAssembly();

                // in case of SingleFile deployment, Assembly.Location is empty.
                if (exeAssembly?.Location.Length == 0)
                {
                    isSingleFile = true;
                    HasEntryAssembly = true;
                }

                if (exeAssembly != null && !isSingleFile)
                {
                    HasEntryAssembly = true;
                    ApplicationUri = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeAssembly.ManifestModule.Name);
                }
                else
                {
                    try
                    {
                        // An EntryAssembly may not be found when running from a custom host.
                        // Try to find the native entry point.
                        using (Process currentProcess = Process.GetCurrentProcess())
                        {
                            ApplicationUri = currentProcess.MainModule?.FileName;
                        }
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ApplicationUri = string.Empty;
                    }
                }
            }

            string externalConfigPath = AppDomain.CurrentDomain.GetData("APP_CONFIG_FILE") as string;
            if (!string.IsNullOrEmpty(externalConfigPath))
            {
                if (Uri.IsWellFormedUriString(externalConfigPath, UriKind.Absolute))
                {
                    Uri externalConfigUri = new Uri(externalConfigPath, UriKind.Absolute);
                    if (externalConfigUri.IsFile)
                    {
                        ApplicationConfigUri = externalConfigUri.LocalPath;
                    }
                }
                else
                {
                    if (!Path.IsPathRooted(externalConfigPath))
                    {
                        externalConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, externalConfigPath);
                    }

                    ApplicationConfigUri = Path.GetFullPath(externalConfigPath);
                }
            }
            else if (!string.IsNullOrEmpty(ApplicationUri))
            {
                string applicationPath = ApplicationUri;
                if (isSingleFile)
                {
                    // on Unix, we want to first append '.dll' extension and on Windows change '.exe' to '.dll'
                    // eventually, in ApplicationConfigUri we will get '{applicationName}.dll.config'
                    applicationPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
                        Path.ChangeExtension(ApplicationUri, ".dll") : ApplicationUri + ".dll";
                }

                ApplicationConfigUri = applicationPath + ConfigExtension;
            }

            // In the case when exePath was explicitly supplied, we will not be able to
            // construct user.config paths, so quit here.
            if (exePath != null) return;

            // Skip expensive initialization of user config file information if requested.
            if (!_includesUserConfig) return;

            bool isHttp = StringUtil.StartsWithOrdinalIgnoreCase(ApplicationConfigUri, HttpUri);
            SetNamesAndVersion(exeAssembly, isHttp);
            if (isHttp) return;

            // Create a directory suffix for local and roaming config of three parts:

            // (1) Company name
            string part1 = Validate(_companyName, limitSize: true);

            // (2) Domain or product name & an application uri hash
            string namePrefix = Validate(AppDomain.CurrentDomain.FriendlyName, limitSize: true);
            if (string.IsNullOrEmpty(namePrefix))
                namePrefix = Validate(ProductName, limitSize: true);
            string applicationUriLower = !string.IsNullOrEmpty(ApplicationUri)
                ? ApplicationUri.ToLowerInvariant()
                : null;
            string hashSuffix = GetTypeAndHashSuffix(applicationUriLower, isSingleFile);
            string part2 = !string.IsNullOrEmpty(namePrefix) && !string.IsNullOrEmpty(hashSuffix)
                ? namePrefix + hashSuffix
                : null;

            // (3) The product version
            string part3 = Validate(ProductVersion, limitSize: false);

            string dirSuffix = CombineIfValid(CombineIfValid(part1, part2), part3);

            string roamingFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (Path.IsPathRooted(roamingFolderPath))
            {
                RoamingConfigDirectory = CombineIfValid(roamingFolderPath, dirSuffix);
                RoamingConfigFilename = CombineIfValid(RoamingConfigDirectory, UserConfigFilename);
            }

            string localFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (Path.IsPathRooted(localFolderPath))
            {
                LocalConfigDirectory = CombineIfValid(localFolderPath, dirSuffix);
                LocalConfigFilename = CombineIfValid(LocalConfigDirectory, UserConfigFilename);
            }
        }

        internal static ClientConfigPaths Current => GetPaths(null, true);

        internal bool HasEntryAssembly { get; }

        internal string ApplicationUri { get; }

        internal string ApplicationConfigUri { get; }

        internal string RoamingConfigFilename { get; }

        internal string RoamingConfigDirectory { get; }

        internal bool HasRoamingConfig => (RoamingConfigFilename != null) || !_includesUserConfig;

        internal string LocalConfigFilename { get; }

        internal string LocalConfigDirectory { get; }

        internal bool HasLocalConfig => (LocalConfigFilename != null) || !_includesUserConfig;

        internal string ProductName { get; private set; }

        internal string ProductVersion { get; private set; }

        internal static ClientConfigPaths GetPaths(string exePath, bool includeUserConfig)
        {
            ClientConfigPaths result;

            if (exePath == null)
            {
                if ((s_current == null) || (includeUserConfig && !s_currentIncludesUserConfig))
                {
                    s_current = new ClientConfigPaths(null, includeUserConfig);
                    s_currentIncludesUserConfig = includeUserConfig;
                }

                result = s_current;
            }
            else result = new ClientConfigPaths(exePath, includeUserConfig);

            return result;
        }

        internal static void RefreshCurrent()
        {
            s_currentIncludesUserConfig = false;
            s_current = null;
        }

        // Combines path2 with path1 if possible, else returns null.
        private static string CombineIfValid(string path1, string path2)
        {
            if ((path1 == null) || (path2 == null)) return null;

            try
            {
                return Path.Combine(path1, path2);
            }
            catch
            {
                return null;
            }
        }

        // Returns a type and hash suffix based on what used to come from app domain evidence.
        // The evidence we use, in priority order, is Strong Name, Url and Exe Path. If one of
        // these is found, we compute a SHA1 hash of it and return a suffix based on that.
        // If none is found, we return null.
        [UnconditionalSuppressMessage("SingleFile", "IL3002: RequiresAssemblyFiles on Module.Name",
            Justification = "Code handles single file case")]
        private static string GetTypeAndHashSuffix(string exePath, bool isSingleFile)
        {
            Assembly assembly = Assembly.GetEntryAssembly();

            string suffix = null;
            string typeName = null;
            string hash = null;

            if (assembly != null && !isSingleFile)
            {
                AssemblyName assemblyName = assembly.GetName();

                try
                {
                    // Certain platforms may not have support for crypto
                    hash = IdentityHelper.GetNormalizedStrongNameHash(assemblyName);
                }
                catch (PlatformNotSupportedException) { }

                if (hash != null)
                {
                    typeName = StrongNameDesc;
                }
                else if (Uri.TryCreate(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, assembly.ManifestModule.Name), UriKind.Absolute, out Uri codeBase))
                {
                    try
                    {
                        // Certain platforms may not have support for crypto
                        hash = IdentityHelper.GetNormalizedUriHash(codeBase);
                        typeName = UrlDesc;
                    }
                    catch (PlatformNotSupportedException) { }
                }
            }
            else if (!string.IsNullOrEmpty(exePath))
            {
                try
                {
                    // Fall back on the exe name
                    // Certain platforms may not have support for crypto
                    hash = IdentityHelper.GetStrongHashSuitableForObjectName(exePath);
                    typeName = PathDesc;
                }
                catch (PlatformNotSupportedException) { }
            }

            if (!string.IsNullOrEmpty(hash)) suffix = "_" + typeName + "_" + hash;
            return suffix;
        }

        private void SetNamesAndVersion(Assembly exeAssembly, bool isHttp)
        {
            Type mainType = null;

            // Get CompanyName, ProductName, and ProductVersion
            // First try custom attributes on the assembly.
            if (exeAssembly != null)
            {
                object[] attrs = exeAssembly.GetCustomAttributes(typeof(AssemblyCompanyAttribute), false);
                if ((attrs != null) && (attrs.Length > 0))
                {
                    _companyName = ((AssemblyCompanyAttribute)attrs[0]).Company?.Trim();
                }

                attrs = exeAssembly.GetCustomAttributes(typeof(AssemblyProductAttribute), false);
                if ((attrs != null) && (attrs.Length > 0))
                {
                    ProductName = ((AssemblyProductAttribute)attrs[0]).Product?.Trim();
                }

                ProductVersion = exeAssembly.GetName().Version.ToString().Trim();
            }

            // If we couldn't get custom attributes, fall back on the entry type namespace
            if (!isHttp &&
                (string.IsNullOrEmpty(_companyName) || string.IsNullOrEmpty(ProductName) ||
                string.IsNullOrEmpty(ProductVersion)))
            {
                if (exeAssembly != null)
                {
                    MethodInfo entryPoint = exeAssembly.EntryPoint;
                    if (entryPoint != null)
                    {
                        mainType = entryPoint.ReflectedType;
                    }
                }

                string ns = null;
                if (mainType != null) ns = mainType.Namespace;

                if (string.IsNullOrEmpty(ProductName))
                {
                    // Try the remainder of the namespace
                    if (ns != null)
                    {
                        int lastDot = ns.LastIndexOf('.');
                        if ((lastDot != -1) && (lastDot < ns.Length - 1)) ProductName = ns.Substring(lastDot + 1);
                        else ProductName = ns;

                        ProductName = ProductName.Trim();
                    }

                    // Try the type of the entry assembly
                    if (string.IsNullOrEmpty(ProductName) && (mainType != null)) ProductName = mainType.Name.Trim();

                    // give up, return empty string
                    if (ProductName == null) ProductName = string.Empty;
                }

                if (string.IsNullOrEmpty(_companyName))
                {
                    // Try the first part of the namespace
                    if (ns != null)
                    {
                        int firstDot = ns.IndexOf('.');
                        _companyName = firstDot != -1 ? ns.Substring(0, firstDot) : ns;

                        _companyName = _companyName.Trim();
                    }

                    // If that doesn't work, use the product name
                    if (string.IsNullOrEmpty(_companyName)) _companyName = ProductName;
                }
            }

            // Desperate measures for product version - assume 1.0
            if (string.IsNullOrEmpty(ProductVersion)) ProductVersion = "1.0.0.0";
        }

        // Makes the passed in string suitable to use as a path name by replacing illegal characters
        // with underscores. Additionally, we do two things - replace spaces too with underscores and
        // limit the resultant string's length to MaxLengthToUse if limitSize is true.
        private static string Validate(string str, bool limitSize)
        {
            string validated = str;

            if (string.IsNullOrEmpty(validated)) return validated;

            // First replace all illegal characters with underscores
            foreach (char c in Path.GetInvalidFileNameChars()) validated = validated.Replace(c, '_');

            // Replace all spaces with underscores
            validated = validated.Replace(' ', '_');

            if (limitSize)
            {
                validated = validated.Length > MaxLengthToUse
                    ? validated.Substring(0, MaxLengthToUse)
                    : validated;
            }

            return validated;
        }
    }
}
