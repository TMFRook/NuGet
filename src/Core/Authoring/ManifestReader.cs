﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using NuGet.Resources;

namespace NuGet
{
    internal static class ManifestReader
    {
        public static Manifest ReadManifest(XDocument document)
        {
            return new Manifest
            {
                Metadata = ReadMetadata(document.Root.ElementsNoNamespace("metadata").First()),
                Files = ReadFilesList(document.Root.ElementsNoNamespace("files").FirstOrDefault())
            };
        }

        private static ManifestMetadata ReadMetadata(XElement xElement)
        {
            var manifestMetadata = new ManifestMetadata();
            manifestMetadata.DependencySets = new List<ManifestDependencySet>();

            XNode node = xElement.FirstNode;
            while (node != null)
            {
                var element = node as XElement;
                if (element != null)
                {
                    ReadMetadataValue(manifestMetadata, element);
                }
                node = node.NextNode;
            }

            return manifestMetadata;
        }

        private static void ReadMetadataValue(ManifestMetadata manifestMetadata, XElement element)
        {
            if (element.Value == null)
            {
                return;
            }

            string value = element.Value.SafeTrim();
            switch (element.Name.LocalName)
            {
                case "id":
                    manifestMetadata.Id = value;
                    break;
                case "version":
                    manifestMetadata.Version = value;
                    break;
                case "authors":
                    manifestMetadata.Authors = value;
                    break;
                case "owners":
                    manifestMetadata.Owners = value;
                    break;
                case "licenseUrl":
                    manifestMetadata.LicenseUrl = value;
                    break;
                case "projectUrl":
                    manifestMetadata.ProjectUrl = value;
                    break;
                case "iconUrl":
                    manifestMetadata.IconUrl = value;
                    break;
                case "requireLicenseAcceptance":
                    manifestMetadata.RequireLicenseAcceptance = XmlConvert.ToBoolean(value);
                    break;
                case "description":
                    manifestMetadata.Description = value;
                    break;
                case "summary":
                    manifestMetadata.Summary = value;
                    break;
                case "releaseNotes":
                    manifestMetadata.ReleaseNotes = value;
                    break;
                case "copyright":
                    manifestMetadata.Copyright = value;
                    break;
                case "language":
                    manifestMetadata.Language = value;
                    break;
                case "title":
                    manifestMetadata.Title = value;
                    break;
                case "tags":
                    manifestMetadata.Tags = value;
                    break;
                case "dependencies":
                    manifestMetadata.DependencySets = ReadDependencySet(element);
                    break;
                case "frameworkAssemblies":
                    manifestMetadata.FrameworkAssemblies = ReadFrameworkAssemblies(element);
                    break;
                case "references":
                    manifestMetadata.References = ReadReferences(element);
                    break;
            }
        }

        private static List<ManifestReference> ReadReferences(XElement referenceElement)
        {
            if (!referenceElement.HasElements)
            {
                return new List<ManifestReference>(0);
            }

            return (from element in referenceElement.Elements()
                    select new ManifestReference { File = element.GetOptionalAttributeValue("file").SafeTrim() }
                    ).ToList();
        }

        private static List<ManifestFrameworkAssembly> ReadFrameworkAssemblies(XElement frameworkElement)
        {
            if (!frameworkElement.HasElements)
            {
                return new List<ManifestFrameworkAssembly>(0);
            }

            return (from element in frameworkElement.Elements()
                    select new ManifestFrameworkAssembly
                    {
                        AssemblyName = element.GetOptionalAttributeValue("assemblyName").SafeTrim(),
                        TargetFramework = element.GetOptionalAttributeValue("targetFramework").SafeTrim()
                    }).ToList();
        }

        private static List<ManifestDependencySet> ReadDependencySet(XElement dependenciesElement)
        {
            if (!dependenciesElement.HasElements)
            {
                return new List<ManifestDependencySet>();
            }

            // Disallow the <dependencies> element to contain both <dependency> and 
            // <group> child elements. Unfortunately, this cannot be enforced by XSD.
            if (dependenciesElement.ElementsNoNamespace("dependency").Any() &&
                dependenciesElement.ElementsNoNamespace("group").Any())
            {
                throw new InvalidDataException(NuGetResources.Manifest_DependenciesHasMixedElements);
            }

            var dependencies = ReadDependencies(dependenciesElement);
            if (dependencies.Count > 0)
            {
                // old format, <dependency> is direct child of <dependencies>
                var dependencySet = new ManifestDependencySet
                {
                    Dependencies = dependencies
                };
                return new List<ManifestDependencySet> { dependencySet };
            }
            else
            {
                var groups = dependenciesElement.ElementsNoNamespace("group");
                return (from element in groups
                        select new ManifestDependencySet
                        {
                            TargetFramework = element.GetOptionalAttributeValue("targetFramework").SafeTrim(),
                            Dependencies = ReadDependencies(element)
                        }).ToList();
            }
        }

        private static List<ManifestDependency> ReadDependencies(XElement containerElement)
        {
            // element is <dependency>
            return (from element in containerElement.ElementsNoNamespace("dependency")
                    select new ManifestDependency
                    {
                        Id = element.GetOptionalAttributeValue("id").SafeTrim(),
                        Version = element.GetOptionalAttributeValue("version").SafeTrim()
                    }).ToList();
        }

        private static List<ManifestFile> ReadFilesList(XElement xElement)
        {
            if (xElement == null)
            {
                return null;
            }

            List<ManifestFile> files = new List<ManifestFile>();
            foreach (var file in xElement.Elements())
            {
                string target = file.GetOptionalAttributeValue("target").SafeTrim();
                string exclude = file.GetOptionalAttributeValue("exclude").SafeTrim();

                // Multiple sources can be specified by using semi-colon separated values. 
                files.AddRange(from source in file.GetOptionalAttributeValue("src").Trim(';').Split(';')
                               select new ManifestFile { Source = source.SafeTrim(), Target = target.SafeTrim(), Exclude = exclude.SafeTrim() });
            }
            return files;
        }
    }
}