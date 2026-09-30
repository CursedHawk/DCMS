#!/bin/sh
# Packs the plugin SDK -- the abstractions, the kernel types they expose, every plugin's .Api --
# and pushes it to this project's GitLab package registry, where an outside plugin references
# it (docs/plugins.md, "Building a plugin outside this repo"). The patch number is the pipeline
# number; the major is Directory.Build.props' DcmsSdkMajor, which the plugin loader checks.
set -eu

out=artifacts/packages
rm -rf "$out"

for project in \
    src/Shared/Dcms.Shared.Kernel/Dcms.Shared.Kernel.csproj \
    src/PluginSdk/Dcms.PluginSdk.Abstractions/Dcms.PluginSdk.Abstractions.csproj \
    src/Plugins/*.Api/*.csproj
do
    dotnet pack "$project" -c Release -o "$out" -p:DcmsBuildNumber="${CI_PIPELINE_IID:-0}"
done

dotnet nuget add source "${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/packages/nuget/index.json" \
    --name gitlab --username gitlab-ci-token --password "$CI_JOB_TOKEN" --store-password-in-clear-text
# --skip-duplicate: a retried job re-pushes the same versions.
dotnet nuget push "$out/*.nupkg" --source gitlab --skip-duplicate
