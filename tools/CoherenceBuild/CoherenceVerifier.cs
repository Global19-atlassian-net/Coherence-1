using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;

namespace CoherenceBuild
{
    public class CoherenceVerifier
    {
        private readonly IEnumerable<PackageInfo> _packages;
        private readonly Dictionary<string, PackageInfo> _packageLookup;
        private readonly CoherenceVerifyBehavior _verifyBehavior;

        private readonly HashSet<string> PackagesToSkipVerification = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.VisualStudio.Web.BrowserLink",
            "Microsoft.VisualStudio.Web.BrowserLink.Loader",
            "Microsoft.EntityFrameworkCore.Tools",
            "Microsoft.EntityFrameworkCore.Tools.DotNet",
            "Microsoft.Extensions.DotnetToolDispatcher.Sources",
            // begin aspnet/DotNetTools - these build against .NET Core 1.0
            "Microsoft.DotNet.Watcher.Tools",
            "Microsoft.Extensions.Caching.SqlConfig.Tools",
            "Microsoft.Extensions.SecretManager.Tools",
            // Scaffolding
            "Microsoft.VisualStudio.Web.CodeGeneration",
            "Microsoft.VisualStudio.Web.CodeGeneration.Utils",
            "Microsoft.VisualStudio.Web.CodeGeneration.EntityFrameworkCore",
            "Microsoft.VisualStudio.Web.CodeGeneration.Design",
            "Microsoft.VisualStudio.Web.CodeGeneration.Core",
            "Microsoft.VisualStudio.Web.CodeGeneration.Tools",
            "Microsoft.VisualStudio.Web.CodeGeneration.Templating",
            // Non-shipping tools
            "RazorPageGenerator",
        };

        public CoherenceVerifier(
            IEnumerable<PackageInfo> packages,
            CoherenceVerifyBehavior verifyBehavior)
        {
            _packages = packages;
            _packageLookup = new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in packages)
            {
                PackageInfo existingPackage;
                if (_packageLookup.TryGetValue(package.Identity.Id, out existingPackage))
                {
                    throw new Exception("Multiple copies of the following package were found: " +
                        Environment.NewLine +
                        existingPackage +
                        Environment.NewLine +
                        package);
                }

                _packageLookup[package.Identity.Id] = package;
            }
            _verifyBehavior = verifyBehavior;
        }

        public bool VerifyAll()
        {
            if (_verifyBehavior == CoherenceVerifyBehavior.None)
            {
                // Disabled sanity check.
                return true;
            }

            foreach (var packageInfo in _packages)
            {
                Visit(packageInfo);
            }

            var success = true;
            var warnings = new List<string>();
            var errors = new List<string>();

            foreach (var packageInfo in _packages)
            {
                foreach (var mismatch in packageInfo.DependencyMismatches)
                {
                    var message = $"{packageInfo.Identity} depends on {mismatch.Dependency.Id} " +
                        $"v{mismatch.Dependency.VersionRange} ({mismatch.TargetFramework}) when the latest build is v{mismatch.Info.Identity.Version}.";

                    if ((mismatch.Info.IsPartnerPackage && !_verifyBehavior.HasFlag(CoherenceVerifyBehavior.PartnerPackages)) ||
                        (!mismatch.Info.IsPartnerPackage && !_verifyBehavior.HasFlag(CoherenceVerifyBehavior.ProductPackages)))
                    {
                        warnings.Add(message);
                    }
                    else
                    {
                        errors.Add(message);
                    }
                }
            }

            if (warnings.Any())
            {
                Console.WriteLine("Following packages have mismatches but are not failures due to disabled verifications:");
                foreach (var warning in warnings)
                {
                    Log.WriteWarning(warning);
                }
            }

            if (errors.Any())
            {
                foreach (var error in errors)
                {
                    Log.WriteError(error);
                }
                success = false;
            }

            return success;
        }

        private void Visit(PackageInfo packageInfo)
        {
            if (packageInfo.IsPartnerPackage)
            {
                Log.WriteInformation($"Skipping verification for external package {packageInfo.Identity}.");
                return;
            }
            else if (packageInfo.IsLineupPackage)
            {
                Log.WriteInformation($"Skipping verification for lineup package {packageInfo.Identity}.");
                return;
            }
            else if (PackagesToSkipVerification.Contains(packageInfo.Identity.Id))
            {
                Log.WriteWarning($"Skipping verification for package {packageInfo.Identity} because it is in ignore list.");
                return;
            }

            Log.WriteInformation($"Processing package {packageInfo.Identity}");
            try
            {
                foreach (var dependencySet in packageInfo.PackageDependencyGroups)
                {
                    // If the package doens't target any frameworks, just accept it
                    if (dependencySet.TargetFramework == null)
                    {
                        continue;
                    }

                    // Skip PCL frameworks for verification
                    if (dependencySet.TargetFramework.IsPCL)
                    {
                        continue;
                    }

                    foreach (var dependency in dependencySet.Packages)
                    {
                        PackageInfo dependencyPackageInfo;
                        if (!_packageLookup.TryGetValue(dependency.Id, out dependencyPackageInfo))
                        {
                            // External dependency
                            continue;
                        }

                        if (dependencyPackageInfo.Identity.Version != dependency.VersionRange.MinVersion)
                        {
                            // For any dependency in the universe
                            // Add a mismatch if the min version doesn't work out
                            // (we only really care about >= minVersion)
                            packageInfo.DependencyMismatches.Add(new DependencyWithIssue
                            {
                                Dependency = dependency,
                                TargetFramework = dependencySet.TargetFramework,
                                Info = dependencyPackageInfo
                            });
                        }

                        PackageInfo dependencyInfo;
                        if (_packageLookup.TryGetValue(dependency.Id, out dependencyInfo) && !dependencyInfo.IsPartnerPackage)
                        {
                            packageInfo.ProductDependencies.Add(dependencyPackageInfo);
                        }
                    }
                }
            }
            catch
            {
                Log.WriteError($"Unable to verify package {packageInfo.Identity}");
                throw;
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
