// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import {Artifact, Cmd, Transformer, Tool} from "Sdk.Transformers";

const root = d`.`;
const dynamicCodeCovString = "DynamicCodeCov";

@@public
export const qTestTool: Transformer.ToolDefinition = {
    exe: f`${root}/bin/DBS.QTest.exe`,
    description: "CloudBuild QTest",
    runtimeDependencies: globR(d`${root}/bin`, "*"),
    untrackedDirectoryScopes: addIfLazy(Context.getCurrentHost().os === "win", () => [
        d`${Context.getMount("ProgramData").path}`, 
        d`${Context.getMount("ProgramFilesX86").path}`, 
        d`${Context.getMount("ProgramFiles").path}`, 
        d`${Context.getMount("AppData").path}`, 
        d`${Context.getMount("LocalAppData").path}`
    ]),
    dependsOnWindowsDirectories: true,
    dependsOnAppDataDirectory: true,
    prepareTempDirectory: true,
};
const defaultArgs: QTestArguments = {
    testAssembly: undefined,
    qTestType: undefined,
    useVsTest150: true,
    qTestPlatform: QTestPlatform.unspecified,
    qTestDotNetFramework: QTestDotNetFramework.unspecified,
    tags: [
        'test',
        'telemetry:QTest'
    ]
};

const enum CoverageOptions {
    None,
    DynamicFull,
    DynamicChangeList
}

function getCodeCoverageOption(): CoverageOptions {
    if (Environment.hasVariable("[Sdk.BuildXL.CBInternal]CodeCoverageOption")) {
        switch (Environment.getStringValue("[Sdk.BuildXL.CBInternal]CodeCoverageOption")) {
            case CoverageOptions.DynamicChangeList.toString():
                return CoverageOptions.DynamicChangeList;
            case CoverageOptions.DynamicFull.toString():
                return CoverageOptions.DynamicFull;
            default:
                return CoverageOptions.None;
        }
    }
    return CoverageOptions.None;
}

function qTestTypeToString(args: QTestArguments) {
    switch (args.qTestType) {
        case QTestType.msTest_latest:
            if(args.useVsTest150) {
                return "MsTest_150";
            }

            return "MsTest_Latest";
        default:
            Contract.fail("Invalid value specified for macro QTestType");
    };
}
function qTestPlatformToString(qTestPlatform: QTestPlatform) {
    switch (qTestPlatform) {
        case QTestPlatform.x86:
            return "X86";
        case QTestPlatform.x64:
            return "X64";
        case QTestPlatform.arm:
            return "Arm";
        case QTestPlatform.unspecified:
        default:
            return "Unspecified";
    };
}
function qTestDotNetFrameworkToString(qTestDotNetFramework: QTestDotNetFramework) {
    switch (qTestDotNetFramework) {
        case QTestDotNetFramework.framework40:
            return "Framework40";
        case QTestDotNetFramework.framework45:
            return "Framework45";
        case QTestDotNetFramework.framework46:
            return "Framework46";
        case QTestDotNetFramework.frameworkCore10:
            return "FrameworkCore10";
        case QTestDotNetFramework.frameworkCore20:
            return "FrameworkCore20";
        case QTestDotNetFramework.frameworkCore21:
            return "FrameworkCore21";
        case QTestDotNetFramework.frameworkCore22:
            return "FrameworkCore22";
        case QTestDotNetFramework.frameworkCore30:
            return "FrameworkCore30";
        case QTestDotNetFramework.unspecified:
        default:
            return "Unspecified";
    };
}
function validateArguments(args: QTestArguments): void {
    if (args.qTestDirToDeploy && args.qTestInputs) {
        Contract.fail("Do not specify both qTestDirToDeploy and qTestInputs. Specify your inputs using only one of these arguments");
    }
}

/**
 * Evaluate (i.e. schedule) QTest runner with specified arguments.
 */
@@public
export function runQTest(args: QTestArguments): Result {
    args = Object.merge<QTestArguments>(defaultArgs, args);
    validateArguments(args);

    let logDir = args.qTestLogs || Context.getNewOutputDirectory("qtestlogs");
    let consolePath = p`${logDir}/qtest.stdout`;
    let qtestRunTempDirectory = Context.getTempDirectory("qtestRunTemp");
    // When invoked to run multiple attempts, QTest makes copies of sandbox
    // for each run. To ensure the sandbox does not throw access violations, 
    // actual sandbox is designed to be a folder inside sandboxDir
    let sandboxDir = Context.getNewOutputDirectory("sandbox");
    let qtestSandboxInternal = p`${sandboxDir}/qtest`;

    // If qTestInputs is used to deploy files needed to run the test, 
    // We need to create a deployment directory name qtestdirtodeploy and copy these files into it.
    // Then the copied files are passed as dependecy as input to the qtest pip
    let qTestDirToDeploy = undefined;
    if (args.qTestInputs) {
        let qTestDirtoDeployCreated = Context.getNewOutputDirectory("qtestRun");
        let copiedFiles = args.qTestInputs.map(
            f => Transformer.copyFile(
                f,
                p`${qTestDirtoDeployCreated}/${f.name}`,
                args.tags
            )
        );
        qTestDirToDeploy = Transformer.sealDirectory({
            root: qTestDirtoDeployCreated,
            files: copiedFiles,
            scrub: true,
        });
    } 

    // If no qTestInputs is specified, use the qTestDirToDeploy
    qTestDirToDeploy = qTestDirToDeploy || args.qTestDirToDeploy;

    // Microsoft internal cloud service use only
    // TODO: Renaming the internal flag passing from GBR, will remove the old one when the new one roll out from GBR
    let qTestContextInfoFile = Environment.getFileValue("[Sdk.BuildXL.CBInternal]qtestContextInfo") || Environment.getFileValue("[Sdk.BuildXL]qtestContextInfo");
    let qTestContextInfoFilePath = Environment.getPathValue("[Sdk.BuildXL.CBInternal]qtestContextInfo") || Environment.getPathValue("[Sdk.BuildXL]qtestContextInfo");                

    let codeCoverageOption = getCodeCoverageOption();
    let changeAffectedInputListWrittenFile = undefined;
    let changeAffectedInputListWrittenFileArg = {};
    if (codeCoverageOption === CoverageOptions.DynamicChangeList) {
        const parentDir = d`${logDir}`.parent;
        const leafDir = d`${logDir}`.nameWithoutExtension;
        const dir = d`${parentDir}/changeAffectedInput/${leafDir}`;
        changeAffectedInputListWrittenFile = p`${dir}/fileWithImpactedTargets.txt`;
        changeAffectedInputListWrittenFileArg = {changeAffectedInputListWrittenFile : changeAffectedInputListWrittenFile};
    }

    let qCodeCoverageEnumType = (codeCoverageOption === CoverageOptions.DynamicChangeList || codeCoverageOption === CoverageOptions.DynamicFull) ? dynamicCodeCovString :  CoverageOptions.None.toString();

    // Keep this for dev build. Office has a requirement to run code coverage for dev build and open the result with VS.
    qCodeCoverageEnumType = Environment.hasVariable("[Sdk.BuildXL]qCodeCoverageEnumType") ? Environment.getStringValue("[Sdk.BuildXL]qCodeCoverageEnumType") : qCodeCoverageEnumType;

    let commandLineArgs: Argument[] = [
        Cmd.option("--testBinary ", args.testAssembly),
        Cmd.option(
            "--runner ",
            qTestTypeToString(args)
        ),
        Cmd.option(
            "--sandbox ",
            Artifact.none(qtestSandboxInternal)
        ),
        Cmd.option(
            "--copyToSandbox ",
            Artifact.input(qTestDirToDeploy)
        ),
        Cmd.option(
            "--qTestLogsDir ",
            Artifact.output(logDir)
        ),
        Cmd.option(
            "--qtestAdapterPath ",
            Artifact.input(args.qTestAdapterPath)
        ),
        Cmd.option(
            "--qtestPlatform ",
            qTestPlatformToString(args.qTestPlatform)
        ),
        Cmd.option(
            "--qtestDotNetFramework ",
            qTestDotNetFrameworkToString(args.qTestDotNetFramework)
        ),
        Cmd.flag("--qTestRetryOnFailure", args.qTestRetryOnFailure),
        Cmd.option("--qTestAttemptCount ", args.qTestAttemptCount),
        Cmd.option("--qTestTimeoutSec ", args.qTestTimeoutSec),
        Cmd.option(
            "--vstestSettingsFile ", 
            qCodeCoverageEnumType === dynamicCodeCovString && args.vstestSettingsFileForCoverage !== undefined
                ? Artifact.input(args.vstestSettingsFileForCoverage) 
                : Artifact.input(args.vstestSettingsFile)
        ),
        Cmd.option(
            "--qTestRawArgFile ",
            Artifact.input(args.qTestRawArgFile)
        ),
        Cmd.option("--qCodeCoverageEnumType ", qCodeCoverageEnumType),
        Cmd.flag("--zipSandbox", Environment.hasVariable("BUILDXL_IS_IN_CLOUDBUILD")),
        Cmd.flag("--debug", Environment.hasVariable("[Sdk.BuildXL]debugQTest")),
        Cmd.flag("--qTestIgnoreQTestSkip", args.qTestIgnoreQTestSkip),
        Cmd.option("--qTestAdditionalOptions ", args.qTestAdditionalOptions, args.qTestAdditionalOptions ? true : false),
        Cmd.option("--qTestContextInfo ", qTestContextInfoFilePath),
        Cmd.option("--qTestBuildType ", args.qTestBuildType || "unset"),
        Cmd.option("--testSourceDir ", args.testSourceDir),
        Cmd.option("--buildSystem ", "BuildXL"),
        Cmd.option("--QTestCcTargetsFile  ", changeAffectedInputListWrittenFile),       
        Cmd.option("--qTestExcludeCcTargetsFile ", args.qTestExcludeCcTargetsFile)
    ];          

    let unsafeOptions = {
        untrackedPaths: [
            ...addIf(qTestContextInfoFile !== undefined, qTestContextInfoFile),
        ],
        untrackedScopes: [
            // Untracking Recyclebin here to primarily unblock user scenarios that
            // deal with soft-delete and restoration of files from recycle bin.
            d`${sandboxDir.pathRoot}/$Recycle.Bin`,
        ],
        requireGlobalDependencies: true,
    };

    let result = Transformer.execute(
        Object.merge<Transformer.ExecuteArguments>(
        {
            tool: args.qTestTool ? args.qTestTool : qTestTool,
            tags: args.tags,
            description: args.description,
            arguments: commandLineArgs,
            consoleOutput: consolePath,
            workingDirectory: sandboxDir,
            tempDirectory: qtestRunTempDirectory,
            weight: args.weight,
            environmentVariables: [
                ...(args.qTestEnvironmentVariables || [])
            ],
            disableCacheLookup: Environment.getFlag("[Sdk.BuildXL]qTestForceTest"),
            additionalTempDirectories : [sandboxDir],
            privilegeLevel: args.privilegeLevel,
            dependencies: [
                //When there are test failures, and PDBs are looked up to generate the stack traces,
                //the original location of PDBs is used instead of PDBs in test sandbox. This is
                //a temporary solution until a permanent fix regarding the lookup is identified
                ...(args.qTestInputs ? args.qTestInputs.filter(
                    f => f.name.hasExtension && f.name.extension === a`.pdb`
                ) : []),
                ...(args.qTestRuntimeDependencies || []),
            ],
            unsafe: unsafeOptions,
            retryExitCodes: [2],
        },
        changeAffectedInputListWrittenFileArg
    ));

    const qTestLogsDir: StaticDirectory = result.getOutputDirectory(logDir);

    // If code coverage is enabled, schedule a pip that will perform coverage file upload.
    if (qCodeCoverageEnumType === dynamicCodeCovString) {
        const parentDir = d`${logDir}`.parent;
        const leafDir = d`${logDir}`.nameWithoutExtension;
        const coverageLogDir = d`${parentDir}/CoverageLogs/${leafDir}`;
        const coverageConsolePath = p`${coverageLogDir}/coverageUpload.stdout`;
        let qtestCodeCovUploadTempDirectory = Context.getTempDirectory("qtestCodeCovUpload");

        const commandLineArgsForUploadPip: Argument[] = [
            Cmd.option("--qTestLogsDir ", Artifact.output(coverageLogDir)),
            Cmd.option("--qTestContextInfo ", qTestContextInfoFilePath),
            Cmd.option("--coverageDirectory ", Artifact.input(qTestLogsDir)),
            Cmd.option("--qTestBuildType ", args.qTestBuildType || "Unset"),
            Cmd.option("--qtestPlatform ", qTestPlatformToString(args.qTestPlatform))
        ];

        Transformer.execute({
            tool: args.qTestTool ? args.qTestTool : qTestTool,
            tags: args.tags,
            description: "QTest Coverage Upload",
            arguments: commandLineArgsForUploadPip,
            consoleOutput: coverageConsolePath,
            workingDirectory: qtestCodeCovUploadTempDirectory,
            disableCacheLookup: true,
            privilegeLevel: args.privilegeLevel,
            unsafe: unsafeOptions,
            retryExitCodes: [2]
        });
    }

    return <Result>{
        console: result.getOutputFile(consolePath),
        qTestLogs: qTestLogsDir,
    };
}
/**
 * Specifies the type of runner that need to be used to execute tests
 */
@@public
export const enum QTestType {
    /** Uses VsTest 12.0 to execute tests */
    @@Tool.option("--runner MsTest_Latest")
    msTest_latest = 1,
}
/**
 * Specifies the Platform that need to be used to execute tests
 */
@@public
export const enum QTestPlatform {
    @@Tool.option("--qtestPlatform unspecified")
    unspecified = 1,
    @@Tool.option("--qtestPlatform x86")
    x86,
    @@Tool.option("--qtestPlatform x64")
    x64,
    @@Tool.option("--qtestPlatform arm")
    arm,
}
/**
 * Specifies the Framework version that need to be used to execute tests
 */
@@public
export const enum QTestDotNetFramework {
    @@Tool.option("--qtestDotNetFramework unspecified")
    unspecified = 1,
    @@Tool.option("--qtestDotNetFramework framework40")
    framework40,
    @@Tool.option("--qtestDotNetFramework framework45")
    framework45,
    @@Tool.option("--qtestDotNetFramework framework46")
    framework46,
    @@Tool.option("--qtestDotNetFramework frameworkCore10")
    frameworkCore10,
    @@Tool.option("--qtestDotNetFramework frameworkCore20")
    frameworkCore20,
    @@Tool.option("--qtestDotNetFramework frameworkCore21")
    frameworkCore21,
    @@Tool.option("--qtestDotNetFramework frameworkCore22")
    frameworkCore22,
    @@Tool.option("--qtestDotNetFramework frameworkCore30")
    frameworkCore30,
}
/**
 * Arguments of DBS.QTest.exe
 */
// @@toolName("DBS.QTest.exe")
@@public
export interface QTestArguments extends Transformer.RunnerArguments {
    /** Option to specify the location of the the qtest executable. */
    qTestTool?: Transformer.ToolDefinition,
    /** The assembly built from test projects that contain the unit tests. */
    testAssembly: Artifact | Path;
    /** Directory that includes all necessary artifacts to run the test, will be copied to sandbox by QTest */
    qTestDirToDeploy?: StaticDirectory;
    /** Explicit specification of all inputs instead of using qTestDirToDeploy, this file will be copied to sandbox by QTest */
    qTestInputs?: File[];
    /** Explicit specification of extra run time dependencies, will not be copied to sandbox */
    qTestRuntimeDependencies ?: Transformer.InputArtifact[];
    /** Describes the runner to launch tests */
    qTestType?: QTestType;
    /** This makes DBS.QTest.exe use custom test adapters for vstest from a given path in the test run. */
    qTestAdapterPath?: StaticDirectory;
    /** Platform that need to be used to execute tests */
    qTestPlatform?: QTestPlatform;
    /** Framework version that need to be used to execute tests */
    qTestDotNetFramework?: QTestDotNetFramework;
    /** Optional directory where all QTest logs can be written to */
    qTestLogs?: Directory;
    /** Specifies to automatically retry failing tests */
    qTestRetryOnFailure?: boolean;
    /** Executes tests for specified number of times. A test is considered as passed So t
     * only when all attempts pass. Maximum allowed value is 100.*/
    qTestAttemptCount?: number;
    /** Raw arguments that are passed as it is to the underlying test runner */
    qTestRawArgFile?: File;
    /** Maximum runtime allowed for QTests in seconds. Cannot exceed maximum of 600 seconds. */
    qTestTimeoutSec?: number;
    /** Helps ignore the QTestSkip test case filter */
    qTestIgnoreQTestSkip?: boolean;
    /** Helps to use VsTest 15.0 instead of default VsTest 12.0 */
    useVsTest150?: boolean;
    /** Optional arguments that will be passed on to the corresponding test runner. */
    qTestAdditionalOptions?: string;
    /** Path to runsettings file that will be passed on to vstest.console.exe. */
    vstestSettingsFile?: File;
    /** vstestSettingsFileForCoverage instead of vstestSettingsFile will be passed on to vstest.console.exe when code coverage is enabled.*/
    vstestSettingsFileForCoverage?: File;
    /** Optionally override to increase the weight of test pips that require more machine resources */
    weight?: number;
    /** Privilege level required by this process to execute. */
    privilegeLevel?: "standard" | "admin";
    /** Specifies the build type */
    qTestBuildType?: string;
    /** Specifies the environment variables to forward to qtest */
    qTestEnvironmentVariables?: Transformer.EnvironmentVariable[];
    /** Specify the path relative to enlistment root of the sources from which the test target is built */
    testSourceDir?: RelativePath;
    /** Path to a file which contains a list of target file names excluded for code coverage processing*/
    qTestExcludeCcTargetsFile?: Path;
}
/**
 * Test results from a vstest.console.exe run
 */
@@public
export interface Result {
    /** Console output from the test run. */
    console: DerivedFile;
    /** Location of the QTestLogs directory to consume any other outputs of QTest */
    qTestLogs: StaticDirectory;
}
