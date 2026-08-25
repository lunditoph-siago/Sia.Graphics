# Release

The `Publish` workflow performs a complete dry run when started manually. A
version tag such as `v0.1.0-preview.1` runs the same validation and publishes
the seven NuGet packages.

Before the first tag:

1. Create a protected GitHub environment named `nuget.org`.
2. In the Sieluna NuGet.org account, add a Trusted Publishing policy for the
   repository that will run the release, workflow file `nuget.yml`, and
   environment `nuget.org`.
3. Run the workflow manually and require both Windows x64 and Linux x64
   workload smoke tests to pass.
4. Create and push a signed version tag.

No long-lived NuGet API key is used. The release job obtains a short-lived key
through OIDC, attests every package, publishes managed and RID packages first,
and publishes the feature-band workload manifest last.

The first native build takes about 45 minutes per platform. Exact source and
compiler fingerprints restore the validated build trees on later runs; the
measured cached native builds complete in roughly one to two minutes.
