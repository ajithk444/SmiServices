﻿
using CommandLine;
using Microservices.CohortExtractor.Execution;
using Smi.Common.Execution;
using Smi.Common.Options;

namespace Microservices.CohortExtractor
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            return Parser.Default.ParseArguments<CliOptions>(args).MapResult(
                (a) =>
                {
                    GlobalOptions options = GlobalOptions.Load(a);

                    var bootStrapper = new MicroserviceHostBootstrapper(() =>
                        new CohortExtractorHost(options, null, null)); //Use the auditor and request fullfilers specified in the yaml

                    return bootStrapper.Main();
                },
                err => -100); //not parsed
        }
    }
}
