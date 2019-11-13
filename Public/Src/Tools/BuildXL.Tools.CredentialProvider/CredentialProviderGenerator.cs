﻿using System;
using System.Collections.Generic;

namespace BuildXL.Tools.CredentialProvider
{
    class CredentialProviderGenerator
    {
        private readonly static Dictionary<string, string> ValidPackageSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://pkgs.dev.azure.com/1essharedassets/_packaging/BuildXL/nuget/v3/index.json"] = "1ESSHAREDASSETS_BUILDXL_FEED_PAT",
            ["https://pkgs.dev.azure.com/cloudbuild/_packaging/BuildXL.Selfhost/nuget/v3/index.json"] = "CLOUDBUILD_BUILDXL_SELFHOST_FEED_PAT"
        };

        public static int Main(string[] args)
        {
            string uri = GetUri(args);

            if (string.IsNullOrWhiteSpace(uri))
            {
                EmitErrorOutput("The passed Uri value parameter is blank or null");
                return 2;
            }

            if (!ValidPackageSources.TryGetValue(uri, out var envVar))
            {
                EmitErrorOutput("The Uri value parameter is not supported in this credential provider");
                return 1;
            }

            var pat = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(pat))
            {
                EmitErrorOutput($"The value of the env var '{envVar}' is not set, so the credentials for '{uri}' cannot be retrieved.");
                return 1;
            }

            EmitOutput(pat);
            return 0;
        }

        private static string GetUri(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("-uri", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return args[i + 1];
                    }
                    else
                    {
                        return null;
                    }
                }
            }

            return null;
        }

        private static void EmitOutput(string pat)
        {
            // Pats will be printed to the standard output since nuget.exe picks up this infromation from the std output
            // Nuget Credential Provider requirements available here: https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers
            Console.Out.WriteLine($"{{ \"Username\":\"DoesNotReallyMatterForPATs\",\"Password\":\"{pat}\",\"Message\":\"\"}}");
        }

        private static void EmitErrorOutput(string errorText)
        {
            Console.Out.WriteLine("Error:  " + errorText);
        }
    }
}
