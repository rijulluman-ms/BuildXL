// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import {Transformer} from "Sdk.Transformers";

import * as Json from "Sdk.Json";
import * as Shared from "Sdk.Managed.Shared";
import * as Deployment from "Sdk.Deployment";

namespace RuntimeConfigFiles {

    // We currently don't place proper package and assembly versions in the configuration files
    // and hardcode all versions to this version for now.
    const temporaryVersionHack = "1.0.0.0";

    @@public
    export function createFiles(
        framework: Shared.Framework,
        assemblyName: string,
        runtimeBinary: Shared.Binary,
        references: Shared.Reference[],
        runtimeContentToSkip: Deployment.DeployableItem[],
        appConfig: File,
        testRunnerDeployment?: boolean
        ) : File[] {

        const runtimeConfigFolder = Context.getNewOutputDirectory("runtimeConfigFolder");
        switch (framework.runtimeConfigStyle) {

            case "appConfig":
                if (!appConfig) {
                    return undefined; // undefined instead of [] to  prevent looping over empty arrays when deploying to disk, even if the result is the same.
                }

                return [
                    createAppConfig(assemblyName, appConfig, runtimeConfigFolder, "exe"),
                ];

            case "runtimeJson":
                Contract.requires(!String.isUndefinedOrEmpty(framework.runtimeConfigVersion), "Frameworks with runtimeConfigStyle of runtimeJson must specify runtimeConfigVersion");
                Contract.requires(!String.isUndefinedOrEmpty(framework.runtimeFrameworkName), "Frameworks with runtimeConfigStyle of runtimeJson must specify runtimeFrameworkName");

                if (framework.applicationDeploymentStyle === "selfContained" && !testRunnerDeployment) {
                    Contract.requires(qualifier.targetRuntime !== undefined, "Frameworks with applicationDeploymentStyle set to 'selfContained', must know the target runtime to deploy for");
                    Contract.requires(framework.runtimeContentProvider !== undefined, "Frameworks with applicationDeploymentStyle set to 'selfContained', must provide a selfContainedRuntimeContent provider");
                }

                return [
                    createDependenciesJson(framework, assemblyName, runtimeBinary, references, runtimeContentToSkip, testRunnerDeployment),
                    createRuntimeConfigJson(framework, assemblyName, runtimeConfigFolder, testRunnerDeployment),
                ];
            case "none":
                return [];
            default:
                Contract.fail("Unsupported runtime config style: " + framework.runtimeConfigStyle);
                return undefined;
        }
    }

    function createAppConfig(assemblyName: string, appConfig: File, runtimeConfigFolder: Directory, configBaseName: string) : File {
        return Transformer.copyFile(appConfig, p`${runtimeConfigFolder}/${assemblyName + "." + configBaseName + ".config"}`);
    }

    /**
     * .NET Core assemblies need dependency information to be deployed alongside them, this function creates the
     * deps.json file parametrized on the passed assembly.
     */
    function createDependenciesJson(
        framework: Shared.Framework,
        assemblyName: string,
        runtimeBinary: Shared.Binary,
        references: Shared.Reference[],
        runtimeContentToSkip: Deployment.DeployableItem[],
        testRunnerDeployment?: boolean
        ): File {

        const specFileOutput = Context.getNewOutputDirectory("DotNetSpecFiles");
        const runtimeReferences = Helpers.computeTransitiveReferenceClosure(framework, references, runtimeContentToSkip, false);

        const dependencySpecExtension = `${assemblyName}.deps.json`;
        const dependencySpecPath = p`${specFileOutput}/${dependencySpecExtension}`;

        const runtimeName = "runtimepack." + framework.runtimeFrameworkName + ".Runtime." + qualifier.targetRuntime;

        // we seed the target and libraries with the current assembly being compiled
        let targetsSet = {}.overrideKey(`${assemblyName}/${temporaryVersionHack}`, <Object>{
            dependencies: createDependencies(references).overrideKey(runtimeName, framework.runtimeConfigVersion),
            runtime: {}.overrideKey(runtimeBinary.binary.name.toString(), {})
        });

        let librariesSet = {}.overrideKey(`${assemblyName}/${temporaryVersionHack}`, {
            type: "project",
            serviceable: false,
            sha512: "",
        });

        for (let i = 0; i < runtimeReferences.length; i++) {
            const assemblyFile = runtimeReferences[i].binary;

            // TODO: We have to change the way the dependency closure gets created - non assembly files e.g. `.dylib` are often times
            //        runtime dependencies inside the .deps.json file for a specifc assembly. Currently we only have a flat array of binaries.
            //        Skip non-dll files for now, as the CoreCLR does not enforce a full runtime dependency list to be provided for each assembly.
            if (assemblyFile.extension !== a`.dll`) {
                continue;
            }

            const assemblyNameWithoutExtension = `${assemblyFile.nameWithoutExtension}/${temporaryVersionHack}`;
            const assemblyName = `${assemblyFile.name}`;

            targetsSet = targetsSet.overrideKey(assemblyNameWithoutExtension, {
                runtime: {}.overrideKey(assemblyName, {})
            });

            librariesSet = librariesSet.overrideKey(assemblyNameWithoutExtension, {
                type: "project",
                serviceable: false,
                sha512: "",
            });
        }

        // In the case of self-contained deployment, we need to inject the runtime information provided by the framework
        if (framework.applicationDeploymentStyle === "selfContained" && !testRunnerDeployment)
        {
            const fullyQualifiedRuntimeDescription = runtimeName + "/" + framework.runtimeConfigVersion;

            const runtimeContent = framework.runtimeContentProvider(qualifier.targetRuntime);
            const runtimeContentLibraries = runtimeContent.filter(file => file.path.toString().contains("/lib/"));
            const runtimeContentNative = runtimeContent.filter(file => file.path.toString().contains("/native/"));
            const libMarker = "lib/" + qualifier.targetFramework;
            const runtimeMarker = "native/";

            let runtimeInfo = {};
            for (let i = 0; i < runtimeContentLibraries.length; i++) {
                let relativeRuntimePath = runtimeContentLibraries[i].path.toString();

                relativeRuntimePath = relativeRuntimePath.slice(relativeRuntimePath.indexOf(libMarker) + libMarker.length + 1, relativeRuntimePath.length - 1);
                runtimeInfo = runtimeInfo.overrideKey(relativeRuntimePath, {
                    "assemblyVersion": "1.0.0.0",
                    "fileVersion": "1.0.0.0"
                });
            }

            let nativeInfo = {};
            for (let i = 0; i < runtimeContentNative.length; i++) {
                let relativeRuntimePath = runtimeContentNative[i].path.toString();
                relativeRuntimePath = relativeRuntimePath.slice(relativeRuntimePath.indexOf(runtimeMarker) + runtimeMarker.length, relativeRuntimePath.length - 1);
                nativeInfo = nativeInfo.overrideKey(relativeRuntimePath, {
                    "fileVersion": "0.0.0.0"
                });
            }

            targetsSet = targetsSet.overrideKey(fullyQualifiedRuntimeDescription, {
                runtime: runtimeInfo,
                native: nativeInfo,
            });

            librariesSet = librariesSet.overrideKey(fullyQualifiedRuntimeDescription, {
                type: "runtimepack",
                serviceable: false,
                sha512: ""
            });
        }

        let runtimeDependencies = {
            runtimeTarget: {
                name: framework.assemblyInfoTargetFramework,
                signature: ""
            },
            compilationOptions: {},
            targets: {}.overrideKey(framework.assemblyInfoTargetFramework, targetsSet),
            libraries: librariesSet,
        };

        return Json.write(dependencySpecPath, runtimeDependencies, '"');
    }

    function createDependencies(references: Shared.Reference[]) : Object {
        let result = <Object>{};

        for (let ref of references)
        {
            if (Shared.isBinary(ref)) {
                result = result.overrideKey(ref.binary.nameWithoutExtension.toString(), temporaryVersionHack);
            }
            else if (Shared.isAssembly(ref)) {
                result = result.overrideKey(ref.name.toString(), temporaryVersionHack);
            }
            else if (Shared.isManagedPackage(ref)) {
                result = result.overrideKey(ref.name, temporaryVersionHack);
            }
            else {
                Contract.fail("Unexpected reference type to generate .net Core deps.json file");
            }
        }

        return result;
    }

    /**
     * .NET Core assemblies need runtime information to be deployed alongside them, this function creates the
     * runtimeconfig.json parametrized on the passed assembly and runtime version number
     */
    @@public
    export function createRuntimeConfigJson(framework: Shared.Framework, assemblyName: string, runtimeConfigFolder: Directory, testRunnerDeployment?: boolean): File {
        const useRuntimeOptions = testRunnerDeployment || framework.applicationDeploymentStyle !== "selfContained";
        const frameworkRuntimeOptions = useRuntimeOptions ? {
            tfm: framework.targetFramework,
            framework: {
                name: framework.runtimeFrameworkName,
                version: framework.runtimeConfigVersion,
            }
        } : {};

        // when not using Server GC, in large builds the front end is likely to get completely bogged, on Unix we want GC passes
        // happening more often to reduce the overall process memory footprint
        const gcRuntimeOptions = {
            configProperties: {
                "System.GC.Server": Context.getCurrentHost().os === "win",
                "System.GC.RetainVM": Context.getCurrentHost().os === "win"
            },
        };

        let options = {
            runtimeOptions: Object.merge(gcRuntimeOptions, frameworkRuntimeOptions)
        };

        return Json.write(p`${runtimeConfigFolder}/${assemblyName + ".runtimeconfig.json"}`, options, '"');
    }

    @@public
    export function createDllAppConfig(framework: Shared.Framework, assemblyName: string, appConfig: File) : File[] {
        if (framework.runtimeConfigStyle === "appConfig" && appConfig) {
            const runtimeConfigFolder = Context.getNewOutputDirectory("runtimeConfigFolder");
            return [
                createAppConfig(assemblyName, appConfig, runtimeConfigFolder, "dll"),
            ];
        }

        return undefined;
    }
}
