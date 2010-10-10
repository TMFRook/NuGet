﻿namespace NuPack {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Data.Services.Common;
    using System.Data.Services.Client;

    [DataServiceKey("Id", "Version")]
    [EntityPropertyMappingAttribute("Modified", SyndicationItemProperty.Updated, SyndicationTextContentKind.Plaintext, keepInContent: false)]
    [EntityPropertyMappingAttribute("Id", SyndicationItemProperty.Title, SyndicationTextContentKind.Plaintext, keepInContent: false)]
    [EntityPropertyMappingAttribute("Description", SyndicationItemProperty.Summary, SyndicationTextContentKind.Plaintext, keepInContent: false)]
    [EntityPropertyMappingAttribute("Authors", SyndicationItemProperty.AuthorName, SyndicationTextContentKind.Plaintext, keepInContent: false)]
    [CLSCompliant(false)]
    public class DataServicePackage : IPackage {
        private Lazy<IPackage> _retrievedPackage;

        public string Id {
            get;
            set;
        }

        public string Category {
            get;
            set;
        }

        public string Description {
            get;
            set;
        }

        public string Language {
            get;
            set;
        }

        public string LastModifiedBy {
            get;
            set;
        }

        public bool RequireLicenseAcceptance {
            get;
            set;
        }

        public Uri LicenseUrl {
            get;
            set;
        }

        public string Keywords {
            get;
            set;
        }

        public string Authors {
            get;
            set;
        }

        public string Version {
            get;
            set;
        }
        
        IEnumerable<string> IPackage.Keywords {
            get {
                return Keywords.Split(',');
            }
        }

        IEnumerable<string> IPackage.Authors {
            get {
                return Authors.Split(',');
            }
        }

        public DateTime Created {
            get;
            set;
        }

        public DateTime Modified {
            get;
            set;
        }

        Version IPackage.Version {
            get {
                return new Version(Version);
            }
        }

        public string Dependencies {
            get;
            set;
        }

        IEnumerable<PackageDependency> IPackage.Dependencies {
            get {
                return from d in Dependencies.Split(',')
                       let parts = d.Split(':')
                       where parts.Length == 4
                       select PackageDependency.CreateDependency(parts[0],
                                                                 Utility.ParseOptionalVersion(parts[1]),
                                                                 Utility.ParseOptionalVersion(parts[2]),
                                                                 Utility.ParseOptionalVersion(parts[3]));
            }
        }

        public IEnumerable<IPackageAssemblyReference> AssemblyReferences {
            get {
                return _retrievedPackage.Value.AssemblyReferences;
            }
        }
       
        public IEnumerable<IPackageFile> GetFiles() {
            return _retrievedPackage.Value.GetFiles();
        }

        internal void InitializeDownloader(Func<IPackage> downloader) {
            _retrievedPackage = new Lazy<IPackage>(downloader, isThreadSafe: false);
        }
    }
}