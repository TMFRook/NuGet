﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell.Interop;
using NuGet.VisualStudio.Resources;
using MsBuildProject = Microsoft.Build.Evaluation.Project;

namespace NuGet.VisualStudio
{
    [Export(typeof(IPackageRestoreManager))]
    internal class PackageRestoreManager : IPackageRestoreManager
    {
        private static readonly string NuGetTargetsFile = Path.Combine(VsConstants.NuGetSolutionSettingsFolder, "nuget.targets");
        private const string NuGetBuildPackageName = "NuGet.Build";
        private const string NuGetBootstrapperPackageName = "NuGet.Bootstrapper";

        private readonly IFileSystemProvider _fileSystemProvider;
        private readonly IPackageSourceProvider _packageSourceProvider;
        private readonly ISolutionManager _solutionManager;
        private readonly IPackageRepositoryFactory _packageRepositoryFactory;
        private readonly IVsThreadedWaitDialogFactory _waitDialogFactory;
        private readonly IPackageRepository _localCacheRepository;
        private readonly IVsPackageManagerFactory _packageManagerFactory;
        private readonly DTE _dte;
        private readonly ISettings _settings;
        private IPackageRepository _officialNuGetRepository;

        [ImportingConstructor]
        public PackageRestoreManager(
            ISolutionManager solutionManager,
            IFileSystemProvider fileSystemProvider,
            IPackageRepositoryFactory packageRepositoryFactory,
            IVsPackageManagerFactory packageManagerFactory,
            IVsPackageSourceProvider packageSourceProvider,
            IVsPackageInstallerEvents packageInstallerEvents,
            ISettings settings) :
            this(ServiceLocator.GetInstance<DTE>(),
                 solutionManager,
                 fileSystemProvider,
                 packageRepositoryFactory,
                 packageSourceProvider,
                 packageManagerFactory,
                 packageInstallerEvents,
                 MachineCache.Default,
                 ServiceLocator.GetGlobalService<SVsThreadedWaitDialogFactory, IVsThreadedWaitDialogFactory>(),
                 settings)
        {
        }

        internal PackageRestoreManager(
            DTE dte,
            ISolutionManager solutionManager,
            IFileSystemProvider fileSystemProvider,
            IPackageRepositoryFactory packageRepositoryFactory,
            IPackageSourceProvider packageSourceProvider,
            IVsPackageManagerFactory packageManagerFactory,
            IVsPackageInstallerEvents packageInstallerEvents,
            IPackageRepository localCacheRepository,
            IVsThreadedWaitDialogFactory waitDialogFactory,
            ISettings settings)
        {
            Debug.Assert(solutionManager != null);
            _dte = dte;
            _fileSystemProvider = fileSystemProvider;
            _solutionManager = solutionManager;
            _packageRepositoryFactory = packageRepositoryFactory;
            _packageSourceProvider = packageSourceProvider;
            _waitDialogFactory = waitDialogFactory;
            _packageManagerFactory = packageManagerFactory;
            _localCacheRepository = localCacheRepository;
            _settings = settings;
            _solutionManager.ProjectAdded += OnProjectAdded;
            _solutionManager.SolutionOpened += OnSolutionOpenedOrClosed;
            _solutionManager.SolutionClosed += OnSolutionOpenedOrClosed;
            packageInstallerEvents.PackageReferenceAdded += OnPackageReferenceAdded;
        }

        public bool IsCurrentSolutionEnabledForRestore
        {
            get
            {
                if (!_solutionManager.IsSolutionOpen)
                {
                    return false;
                }

                string solutionDirectory = _solutionManager.SolutionDirectory;
                if (String.IsNullOrEmpty(solutionDirectory))
                {
                    return false;
                }

                IFileSystem fileSystem = _fileSystemProvider.GetFileSystem(solutionDirectory);
                return fileSystem.FileExists(NuGetTargetsFile);
            }
        }

        public void EnableCurrentSolutionForRestore(bool fromActivation)
        {
            if (!_solutionManager.IsSolutionOpen)
            {
                throw new InvalidOperationException(VsResources.SolutionNotAvailable);
            }

            if (fromActivation)
            {
                // if not in quiet mode, ask user for confirmation before proceeding
                bool? result = MessageHelper.ShowQueryMessage(
                    VsResources.PackageRestoreConfirmation,
                    VsResources.DialogTitle,
                    showCancelButton: false);
                if (result != true)
                {
                    return;
                }
            }

            Exception exception = null;

            IVsThreadedWaitDialog2 waitDialog;
            _waitDialogFactory.CreateInstance(out waitDialog);
            try
            {
                waitDialog.StartWaitDialog(
                    VsResources.DialogTitle,
                    VsResources.PackageRestoreWaitMessage,
                    String.Empty,
                    varStatusBmpAnim: null,
                    szStatusBarText: null,
                    iDelayToShowDialog: 0,
                    fIsCancelable: false,
                    fShowMarqueeProgress: true);

                if (fromActivation)
                {
                    // only enable package restore consent if this is called as a result of user enabling package restore
                    SetPackageRestoreConsent();
                }

                EnablePackageRestore(fromActivation);
            }
            catch (Exception ex)
            {
                exception = ex;
                ExceptionHelper.WriteToActivityLog(exception);
            }
            finally
            {
                int canceled;
                waitDialog.EndWaitDialog(out canceled);
            }

            if (fromActivation)
            {
                if (exception != null)
                {
                    // show error message
                    MessageHelper.ShowErrorMessage(
                        VsResources.PackageRestoreErrorMessage +
                            Environment.NewLine +
                            Environment.NewLine +
                            ExceptionUtility.Unwrap(exception).Message,
                        VsResources.DialogTitle);
                }
                else
                {
                    // show success message
                    MessageHelper.ShowInfoMessage(
                        VsResources.PackageRestoreCompleted,
                        VsResources.DialogTitle);
                }
            }
        }

        public event EventHandler<PackagesMissingStatusEventArgs> PackagesMissingStatusChanged = delegate { };

        public Task RestoreMissingPackages()
        {
            TaskScheduler uiScheduler;
            try
            {
                uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            }
            catch (InvalidOperationException)
            {
                // this exception occurs during unit tests
                uiScheduler = TaskScheduler.Default;
            }

            Task task = Task.Factory.StartNew(() =>
            {
                IVsPackageManager packageManager = _packageManagerFactory.CreatePackageManager();
                IPackageRepository localRepository = packageManager.LocalRepository;
                var projectReferences = GetAllPackageReferences(packageManager);
                foreach (var reference in projectReferences)
                {
                    if (!localRepository.Exists(reference.Id, reference.Version))
                    {
                        packageManager.InstallPackage(reference.Id, reference.Version, ignoreDependencies: true, allowPrereleaseVersions: true);
                    }
                }
            });

            task.ContinueWith(originalTask =>
            {
                if (originalTask.IsFaulted)
                {
                    ExceptionHelper.WriteToActivityLog(originalTask.Exception);
                }
                else
                {
                    // we don't allow canceling
                    Debug.Assert(!originalTask.IsCanceled);

                    // after we're done with restoring packages, do the check again
                    CheckForMissingPackages();
                }
            }, uiScheduler);

            return task;
        }

        public void CheckForMissingPackages()
        {
            bool missing = IsCurrentSolutionEnabledForRestore && CheckForMissingPackagesCore();
            PackagesMissingStatusChanged(this, new PackagesMissingStatusEventArgs(missing));
        }

        private void EnablePackageRestore(bool fromActivation)
        {
            EnsureNuGetBuild(fromActivation);

            IVsPackageManager packageManager = _packageManagerFactory.CreatePackageManager();
            foreach (Project project in _solutionManager.GetProjects())
            {
                EnablePackageRestore(project, packageManager);
            }
        }

        private void SetPackageRestoreConsent()
        {
            var consent = new PackageRestoreConsent(_settings);
            if (!consent.IsGranted)
            {
                consent.IsGranted = true;
            }
        }

        private void EnablePackageRestore(Project project, IVsPackageManager packageManager)
        {
            var projectManager = packageManager.GetProjectManager(project);
            if (projectManager.LocalRepository.GetPackages().IsEmpty())
            {
                // don't enable package restore for the project if it doesn't have at least one 
                // nuget package installed
                return;
            }

            EnablePackageRestore(project);
        }

        private void EnablePackageRestore(Project project)
        {
            if (project.IsWebSite() || project.IsJavaScriptProject())
            {
                // Can't do anything with Website
                // Also, the Javascript Metro project system has some weird bugs 
                // that cause havoc with the package restore mechanism
                return;
            }

            MsBuildProject buildProject = project.AsMSBuildProject();

            AddSolutionDirProperty(project, buildProject);
            AddNuGetTargets(project, buildProject);
            SetMsBuildProjectProperty(project, buildProject, "RestorePackages", "true");

            if (project.IsJavaScriptProject())
            {
                // JavaScript project requires an extra kick
                // in order to save changes to the project file.
                // TODO: Check with VS team to ask them to fix 
                buildProject.Save();
            }
        }

        private void AddNuGetTargets(Project project, MsBuildProject buildProject)
        {
            string targetsPath = Path.Combine(@"$(SolutionDir)", NuGetTargetsFile);

            // adds an <Import> element to this project file.
            if (buildProject.Xml.Imports == null ||
                buildProject.Xml.Imports.All(import => !targetsPath.Equals(import.Project, StringComparison.OrdinalIgnoreCase)))
            {
                buildProject.Xml.AddImport(targetsPath);
                project.Save();
                buildProject.ReevaluateIfNecessary();
            }
        }

        private void AddSolutionDirProperty(Project project, MsBuildProject buildProject)
        {
            const string solutiondir = "SolutionDir";

            if (buildProject.Xml.Properties == null ||
                buildProject.Xml.Properties.All(p => p.Name != solutiondir))
            {
                string relativeSolutionPath = PathUtility.GetRelativePath(
                    project.FullName,
                    PathUtility.EnsureTrailingSlash(_solutionManager.SolutionDirectory));
                relativeSolutionPath = PathUtility.EnsureTrailingSlash(relativeSolutionPath);

                var solutionDirProperty = buildProject.Xml.AddProperty(solutiondir, relativeSolutionPath);
                solutionDirProperty.Condition =
                    String.Format(
                        CultureInfo.InvariantCulture,
                        @"$({0}) == '' Or $({0}) == '*Undefined*'",
                        solutiondir);

                project.Save();
            }
        }

        private static void SetMsBuildProjectProperty(Project project, MsBuildProject buildProject, string name, string value)
        {
            if (!value.Equals(buildProject.GetPropertyValue(name), StringComparison.OrdinalIgnoreCase))
            {
                buildProject.SetProperty(name, value);
                project.Save();
            }
        }

        private void EnsureNuGetBuild(bool fromActivation)
        {
            string solutionDirectory = _solutionManager.SolutionDirectory;
            string nugetFolderPath = Path.Combine(solutionDirectory, VsConstants.NuGetSolutionSettingsFolder);

            IFileSystem fileSystem = _fileSystemProvider.GetFileSystem(solutionDirectory);

            if (!fileSystem.DirectoryExists(VsConstants.NuGetSolutionSettingsFolder) ||
                !fileSystem.FileExists(NuGetTargetsFile))
            {
                // download NuGet.Build and NuGet.Bootstrapper packages into the .nuget folder
                IPackageRepository repository = _packageSourceProvider.GetAggregate(_packageRepositoryFactory, ignoreFailingRepositories: true);

                // Ensure we have packages before we attempt to add them.
                var installPackages = new [] { GetPackage(repository, NuGetBuildPackageName, fromActivation), 
                                               GetPackage(repository, NuGetBootstrapperPackageName, fromActivation) };
                foreach (var package in installPackages)
                {
                    fileSystem.AddFiles(package.GetFiles(Constants.ToolsDirectory), VsConstants.NuGetSolutionSettingsFolder, preserveFilePath: false);
                }

                // IMPORTANT: do this BEFORE adding the .nuget folder to solution so that 
                // the generated .nuget\nuget.config is included in the solution folder too. 
                DisableSourceControlMode();

                // now add the .nuget folder to the solution as a solution folder.
                _dte.Solution.AddFolderToSolution(VsConstants.NuGetSolutionSettingsFolder, nugetFolderPath);
            }
        }

        /// <summary>
        /// Try to retrieve the package with the specified Id from machine cache first. 
        /// If not found, download it from the specified repository and add to machine cache.
        /// </summary>
        private IPackage GetPackage(IPackageRepository repository, string packageId, bool fromActivation)
        {
            // first, find the package from the remote repository
            IPackage package = repository.FindPackage(packageId, version: null, allowPrereleaseVersions: true, allowUnlisted: false);

            if (package == null && fromActivation)
            {
                // if we can't find the package from the remote repositories, look for it
                // from nuget.org feed, provided that it's not already specified in one of the remote repositories
                if (!ContainsSource(_packageSourceProvider, NuGetConstants.DefaultFeedUrl) &&
                    !ContainsSource(_packageSourceProvider, NuGetConstants.V2LegacyFeedUrl))
                {
                    if (_officialNuGetRepository == null)
                    {
                        _officialNuGetRepository = _packageRepositoryFactory.CreateRepository(NuGetConstants.DefaultFeedUrl);
                    }

                    package = _officialNuGetRepository.FindPackage(packageId, version: null, allowPrereleaseVersions: true, allowUnlisted: false);
                }
            }

            bool fromCache = false;

            // if package == null, we use whatever version is in the machine cache
            IPackage cachedPackage = _localCacheRepository.FindPackage(packageId, package != null ? package.Version : null);
            if (cachedPackage != null)
            {
                var dataServicePackage = package as DataServicePackage;
                if (dataServicePackage != null)
                {
                    var cachedHash = cachedPackage.GetHash(dataServicePackage.PackageHashAlgorithm);
                    if (!dataServicePackage.PackageHash.Equals(cachedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // if the remote package has the same hash as with the one in the machine cache, use the one from machine cache
                        package = cachedPackage;
                        fromCache = true;
                    }
                    else
                    {
                        // if the hash has changed, delete the stale package
                        _localCacheRepository.RemovePackage(cachedPackage);
                    }
                }
                else if (package == null)
                {
                    // in this case, we didn't find the package from remote repository.
                    // fallback to using the one in the machine cache.
                    package = cachedPackage;
                    fromCache = true;
                }
            }

            if (package == null)
            {
                throw new InvalidOperationException(
                            String.Format(
                                CultureInfo.CurrentCulture,
                                VsResources.PackageRestoreDownloadPackageFailed,
                                packageId));
            }

            if (!fromCache)
            {
                _localCacheRepository.AddPackage(package);

                // swap to the Zip package to avoid potential downloading package twice
                package = _localCacheRepository.FindPackage(package.Id, package.Version);
                Debug.Assert(package != null);
            }

            return package;
        }

        private static bool ContainsSource(IPackageSourceProvider provider, string source)
        {
            return provider.GetEnabledPackageSources().Any(p => p.Source.Equals(source, StringComparison.OrdinalIgnoreCase));
        }

        private void DisableSourceControlMode()
        {
            // get the settings for this solution
            var nugetFolder = Path.Combine(_solutionManager.SolutionDirectory, VsConstants.NuGetSolutionSettingsFolder);
            var settings = new Settings(_fileSystemProvider.GetFileSystem(nugetFolder));
            settings.DisableSourceControlMode();
        }

        private void OnProjectAdded(object sender, ProjectEventArgs e)
        {
            if (IsCurrentSolutionEnabledForRestore)
            {
                EnablePackageRestore(e.Project, _packageManagerFactory.CreatePackageManager());
                CheckForMissingPackages();
            }
        }

        private void OnPackageReferenceAdded(IVsPackageMetadata metadata)
        {
            if (IsCurrentSolutionEnabledForRestore)
            {
                var packageMetadata = (VsPackageMetadata)metadata;
                var fileSystem = packageMetadata.FileSystem as IVsProjectSystem;
                if (fileSystem != null)
                {
                    var project = _solutionManager.GetProject(fileSystem.UniqueName);
                    if (project != null)
                    {
                        // in this case, we know that this project has at least one nuget package,
                        // so enable package restore straight away
                        EnablePackageRestore(project);
                    }
                }
            }
        }

        private void OnSolutionOpenedOrClosed(object sender, EventArgs e)
        {
            CheckForMissingPackages();
        }

        private bool CheckForMissingPackagesCore()
        {
            // this can happen during unit tests
            if (_packageManagerFactory == null)
            {
                return false;
            }

            IVsPackageManager packageManager = _packageManagerFactory.CreatePackageManager();
            IPackageRepository localRepository = packageManager.LocalRepository;
            var projectReferences = GetAllPackageReferences(packageManager);
            return projectReferences.Any(reference => !localRepository.Exists(reference.Id, reference.Version));
        }

        /// <summary>
        /// Gets all package references in all projects of the current solution plus package 
        /// references specified in the solution packages.config
        /// </summary>
        private IEnumerable<PackageReference> GetAllPackageReferences(IVsPackageManager packageManager)
        {
            IEnumerable<PackageReference> projectReferences = from project in _solutionManager.GetProjects()
                                                              from reference in
                                                                  GetPackageReferences(
                                                                      packageManager.GetProjectManager(project))
                                                              select reference;

            var localRepository = packageManager.LocalRepository as SharedPackageRepository;
            if (localRepository != null)
            {
                IEnumerable<PackageReference> solutionReferences = localRepository.PackageReferenceFile.GetPackageReferences();
                return projectReferences.Concat(solutionReferences).Distinct();
            }

            return projectReferences.Distinct();
        }

        /// <summary>
        /// Gets the package references of the specified project.
        /// </summary>
        private IEnumerable<PackageReference> GetPackageReferences(IProjectManager projectManager)
        {
            var packageRepository = projectManager.LocalRepository as PackageReferenceRepository;
            if (packageRepository != null)
            {
                return packageRepository.ReferenceFile.GetPackageReferences();
            }

            return new PackageReference[0];
        }
    }
}