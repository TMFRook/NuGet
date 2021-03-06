using System.IO;
using System.Linq;
using Lucene.Net.Index;
using Lucene.Net.Linq;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Ninject;
using Ninject.Modules;
using NuGet.Server.Infrastructure.Lucene;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace NuGet.Server.Infrastructure
{
    public class Bindings : NinjectModule
    {
        public override void Load()
        {
            var packagePathResolver = new OneFolderPerPackageIdPathResolver(PackageUtility.PackagePhysicalPath);
            var fileSystem = new PhysicalFileSystem(PackageUtility.PackagePhysicalPath);
            var packageRepository = new LucenePackageRepository(packagePathResolver, fileSystem);

            Bind<IHashProvider>().To<CryptoHashProvider>();

            Bind<IFileSystem>().ToConstant(fileSystem);
            Bind<IPackagePathResolver>().ToConstant(packagePathResolver);
            Bind<IServerPackageRepository>().ToConstant(packageRepository);
            Bind<ILucenePackageRepository>().ToConstant(packageRepository);
            Bind<PackageService>().ToSelf();
            Bind<IPackageAuthenticationService>().To<PackageAuthenticationService>();

            LoadLucene(fileSystem);
        }

        public void LoadLucene(IFileSystem fileSystem)
        {
            var indexDirectory = OpenLuceneDirectory(PackageUtility.LuceneIndexPhysicalPath);

            var analyzer = new PackageAnalyzer();

            var create = ShouldCreateIndex(indexDirectory);
            var writer = new IndexWriter(indexDirectory, analyzer, create, IndexWriter.MaxFieldLength.UNLIMITED);

            var provider = new LuceneDataProvider(indexDirectory, analyzer, Version.LUCENE_30, writer);

            Bind<IndexWriter>().ToConstant(writer);
            Bind<LuceneDataProvider>().ToConstant(provider);
            Bind<IPackageIndexer>().ToConstant(new PackageIndexer());
            Bind<IQueryable<LucenePackage>>().ToConstant(provider.AsQueryable(() => new LucenePackage(fileSystem)));
            Bind<PackageFileSystemWatcher>().ToConstant(new PackageFileSystemWatcher());

            // Force eager initialization of binding not used by other bindings:
            Kernel.Get<PackageFileSystemWatcher>();
        }

        public static bool ShouldCreateIndex(LuceneDirectory dir)
        {
            var create = false;

            try
            {
                create = !dir.ListAll().Any();
            }
            catch (NoSuchDirectoryException)
            {
                create = true;
            }

            return create;
        }

        private static LuceneDirectory OpenLuceneDirectory(string luceneIndexPath)
        {
            var directoryInfo = new DirectoryInfo(luceneIndexPath);
            return FSDirectory.Open(directoryInfo, new NativeFSLockFactory(directoryInfo));
        }

    }
}
