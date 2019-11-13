// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import {Artifact, Cmd, Transformer} from "Sdk.Transformers";
import * as Shared from "Sdk.Managed.Shared";
import * as Deployment from "Sdk.Deployment";
import * as MacOS from "Sdk.MacOS";

export declare const qualifier: {targetFramework: "netcoreapp3.0"};

const defaultAssemblies: Shared.Assembly[] = createDefaultAssemblies();

function macOSRuntimeExtensions(file: File): boolean {
    return file.extension === a`.dylib` || file.extension === a`.a` || file.extension === a`.dll`;
}

function ignoredAssembly(file: File): boolean {
    // We skip deploying those files from the .NET Core package as we need those very assemblies from their dedicated package
    // to compile our platform abstraction layer, which depends on datatypes present only in the dedicated packages
    return file.name === a`System.IO.FileSystem.AccessControl.dll` ||
           file.name === a`System.Security.AccessControl.dll` ||
           file.name === a`System.Security.Principal.Windows.dll`;
}

const windowsRuntimeFiles = [
    ...importFrom("Microsoft.NETCore.App.Runtime.win-x64").Contents.all.getContent().filter(f => f.extension === a`.dll` && !ignoredAssembly(f)),
    ...importFrom("runtime.win-x64.Microsoft.NETCore.DotNetHostResolver").Contents.all.getContent().filter(f => f.extension === a`.dll`),
    ...importFrom("runtime.win-x64.Microsoft.NETCore.DotNetHostPolicy").Contents.all.getContent().filter(f => f.extension === a`.dll`),
];

const osxRuntimeFiles = [
    ...importFrom("Microsoft.NETCore.App.Runtime.osx-x64").Contents.all.getContent().filter(f => macOSRuntimeExtensions(f) && !ignoredAssembly(f)),
    ...importFrom("runtime.osx-x64.Microsoft.NETCore.DotNetHostResolver").Contents.all.getContent().filter(f => macOSRuntimeExtensions(f)),
    ...importFrom("runtime.osx-x64.Microsoft.NETCore.DotNetHostPolicy").Contents.all.getContent().filter(f => macOSRuntimeExtensions(f)),
];

@@public
export function runtimeContentProvider(runtimeVersion: Shared.RuntimeVersion): File[] {
    switch (runtimeVersion)
    {
        case "osx-x64":
            return osxRuntimeFiles;
        case "win-x64":
        default:
            return windowsRuntimeFiles;
    }
}

@@public
export const framework : Shared.Framework = {
    targetFramework: qualifier.targetFramework,

    supportedRuntimeVersion: "v3.0",
    assemblyInfoTargetFramework: ".NETCoreApp,Version=v3.0",
    assemblyInfoFrameworkDisplayName: ".NET Core App",

    standardReferences: defaultAssemblies,

    requiresPortablePdb: true,

    runtimeConfigStyle: "runtimeJson",
    runtimeFrameworkName: "Microsoft.NETCore.App",
    runtimeConfigVersion: "3.0.0",

    // Deployment style for .NET Core applications currently defaults to self-contained
    applicationDeploymentStyle: "selfContained",
    runtimeContentProvider: runtimeContentProvider,

    conditionalCompileDefines: [
        "NET_CORE",
        "NET_COREAPP",
        "NET_COREAPP_30"
    ],
};

function createDefaultAssemblies() : Shared.Assembly[] {
    const pkgContents = importFrom("Microsoft.NETCore.App.Ref").withQualifier({targetFramework: "netcoreapp3.0"}).Contents.all;
    const netcoreAppPackageContents = pkgContents.contents;
    const dlls = netcoreAppPackageContents.filter(file => file.hasExtension && file.extension === a`.dll`);
    return dlls.map(file  => Shared.Factory.createAssembly(pkgContents, file, "netcoreapp3.0", [], true));
}
