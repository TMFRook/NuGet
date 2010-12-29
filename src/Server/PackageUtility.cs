using System;
using System.Web;
using System.Web.Hosting;

namespace NuGet.Server {
    public class PackageUtility {
        internal static string PackagePhysicalPath = HostingEnvironment.MapPath("~/Packages");

        public static Uri GetPackageUrl(string id, string version, Uri baseUri) {
            return new Uri(baseUri, GetPackageDownloadUrl(id, version));
        }

        public static Uri GetRepostAbuseUrl(string id, string version) {
            HttpContext context = HttpContext.Current;
            if (context == null) {
                return null;
            }

            string appRoot = HttpRuntime.AppDomainAppVirtualPath;
            if (!appRoot.EndsWith("/")) {
                appRoot += "/";
            }

            return new Uri(context.Request.Url, appRoot + "packages/reportabuse?id=" + id + "&version=" + version);
        }

        private static string GetPackageDownloadUrl(string id, string version) {
            string appRoot = HttpRuntime.AppDomainAppVirtualPath;
            if (!appRoot.EndsWith("/")) {
                appRoot += "/";
            }

            return appRoot + "packages/download?p=" + GetPackageFileName(id, version);
        }

        public static string GetPackageFileName(string id, string version) {
            return id + "." + version + Constants.PackageExtension;
        }
    }
}