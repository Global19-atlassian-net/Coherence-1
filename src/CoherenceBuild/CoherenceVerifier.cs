﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using NuGet;

namespace CoherenceBuild
{
    public class CoherenceVerifier
    {
        public static bool VerifyAll(ProcessResult result)
        {
            foreach (var productPackageInfo in result.ProductPackages.Values)
            {
                Visit(productPackageInfo, result);
            }

            var success = true;
            foreach (var packageInfo in result.ProductPackages.Values)
            {
                if (!packageInfo.Success)
                {
                    // Temporary workaround for FileSystemGlobbing used in Runtime.
                    if (packageInfo.Package.Id.Equals("Microsoft.Extensions.Runtime", StringComparison.OrdinalIgnoreCase) &&
                        packageInfo.DependencyMismatches.All(d => d.Dependency.Id.Equals("Microsoft.Extensions.FileSystemGlobbing", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // Temporary workaround for xunit.runner.aspnet used in Microsoft.AspNet.Testing.
                    if (packageInfo.Package.Id.Equals("Microsoft.AspNet.Testing", StringComparison.OrdinalIgnoreCase) &&
                        packageInfo.DependencyMismatches.All(d => d.Dependency.Id.Equals("xunit.runner.aspnet", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (packageInfo.InvalidCoreCLRPackageReferences.Count > 0)
                    {
                        Log.WriteError("{0} has invalid package references:", packageInfo.Package.GetFullName());

                        foreach (var invalidReference in packageInfo.InvalidCoreCLRPackageReferences)
                        {
                            Log.WriteError("Reference {0}({1}) must be changed to be a frameworkAssembly.",
                                invalidReference.Dependency,
                                invalidReference.TargetFramework);
                        }
                    }

                    if (packageInfo.DependencyMismatches.Count > 0)
                    {
                        Log.WriteError("{0} has mismatched dependencies:", packageInfo.Package.GetFullName());

                        foreach (var mismatch in packageInfo.DependencyMismatches)
                        {
                            Log.WriteError("    Expected {0}({1}) but got {2}",
                                mismatch.Dependency,
                                (mismatch.TargetFramework == VersionUtility.UnsupportedFrameworkName ?
                                "DNXCORE50" :
                                VersionUtility.GetShortFrameworkName(mismatch.TargetFramework)),
                                mismatch.Info.Package.Version);
                        }
                    }

                    success = false;
                }
            }

            return success;
        }

        private static void Visit(PackageInfo productPackageInfo, ProcessResult result)
        {
            foreach (var dependencySet in productPackageInfo.Package.DependencySets)
            {
                // Skip PCL frameworks for verification
                if (IsPortableFramework(dependencySet.TargetFramework))
                {
                    continue;
                }

                foreach (var dependency in dependencySet.Dependencies)
                {
                    // For any dependency in the universe
                    PackageInfo dependencyPackageInfo;
                    if (result.ProductPackages.TryGetValue(dependency.Id, out dependencyPackageInfo))
                    {
                        productPackageInfo.ProductDependencies.Add(dependencyPackageInfo);

                        if (dependencyPackageInfo.Package.Version != dependency.VersionSpec.MinVersion)
                        {
                            // Add a mismatch if the min version doesn't work out
                            // (we only really care about >= minVersion)
                            productPackageInfo.DependencyMismatches.Add(new DependencyWithIssue
                            {
                                Dependency = dependency,
                                TargetFramework = dependencySet.TargetFramework,
                                Info = dependencyPackageInfo
                            });
                        }
                    }
                    else if (result.CoreCLRPackages.Keys.Contains(dependency.Id))
                    {
                        var coreclrDependency = result.CoreCLRPackages[dependency.Id].Last();
                        var dependenciesToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "System.Collections.Immutable",
                            "System.Reflection.Metadata",
                            "System.Diagnostics.DiagnosticSource",
                            "System.Numerics.Vectors",
                        };

                        if (dependenciesToIgnore.Contains(dependency.Id))
                        {
                            continue;
                        }

                        if (!string.Equals(dependencySet.TargetFramework.Identifier, "DNXCORE", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(dependencySet.TargetFramework.Identifier, ".NETPlatform", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(dependencySet.TargetFramework.Identifier, ".NETCore", StringComparison.OrdinalIgnoreCase))
                        {
                            productPackageInfo.InvalidCoreCLRPackageReferences.Add(new DependencyWithIssue
                            {
                                Dependency = dependency,
                                TargetFramework = dependencySet.TargetFramework,
                                Info = coreclrDependency
                            });
                        }
                    }
                }
            }
        }

        private static bool IsPortableFramework(FrameworkName framework)
        {
            // The profile part has been verified in the ParseFrameworkName() method.
            // By the time it is called here, it's guaranteed to be valid.
            // Thus we can ignore the profile part here
            return framework != null && ".NETPortable".Equals(framework.Identifier, StringComparison.OrdinalIgnoreCase);
        }
    }
}
