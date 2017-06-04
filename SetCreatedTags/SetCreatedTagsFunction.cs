using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Azure;
using Microsoft.Azure.Management.Resources;
using Microsoft.Azure.Management.Resources.Models;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using System.Linq;
using System.Threading.Tasks;
using System.Configuration;
using System.Diagnostics;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.ServiceBus;


namespace SetCreatedTags
{
    public static class SetCreatedTagsFunction
    {
        private static string subscriptionId = ConfigurationManager.AppSettings["SubscriptionId"];
        private static string clientId = ConfigurationManager.AppSettings["ClientId"];
        private static string clientSecret = ConfigurationManager.AppSettings["ClientSecret"];
        private static string tenantId = ConfigurationManager.AppSettings["TenantId"];
        private const string azureManagementUrl = "https://management.azure.com/";

        [FunctionName("SetCreatedTagsFunction")]
        public static void Run([EventHubTrigger("insights-operational-logs", Connection = "insights-operational-logs_EVENTHUB")]string eventHubMessage, TraceWriter log)
        {


            log.Info($"C# Event Hub trigger function processed a message");
            if (eventHubMessage.Length == 0)
            {
                return;
            }

            TokenCloudCredentials aadTokenCredentials =
                    new TokenCloudCredentials(
                        subscriptionId,
                        GetAuthorizationHeaderNoPopup());
            var client = new ResourceManagementClient(aadTokenCredentials);

            var messageJson = JObject.Parse(eventHubMessage);
            foreach (var record in messageJson["records"])
            {

                string operationName = record["operationName"].Value<string>();
                if (operationName.ToLower().EndsWith("/write") && !operationName.ToLower().EndsWith("/deployments/write"))
                {
                    string resourceId = record["identity"]["authorization"]["scope"].Value<string>();
                    string resultType = record["resultType"].Value<string>();
                    DateTime time = record["time"].Value<DateTime>();
                    string appId = record["identity"]["claims"]["appid"].Value<string>();
                    if (appId == clientId)
                    {
                        // Prevent an endless loop by ignoring events generated by the script's service principal
                        log.Info("Ignoring Write event from the service principal that this script uses.");
                        return;
                    }
                    string user = record["identity"]["claims"]["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn"]?.Value<string>();
                    if (user == null)
                    {
                        // Resource was changed by a service principal, not a user
                        user = appId;
                    }
                    log.Info($"Received event from {operationName} : {resourceId} {resultType} {user} at {time}");
                    if (resultType.ToLower() == "success")
                    {
                        var resourceDetails = GetReourceDetails(client, resourceId, log);
                        var resource = client.Resources.Get(resourceDetails.ResourceGroupName, resourceDetails.ResourceIdentity).Resource;
                        var updatedResource = new GenericResource(resource.Location);
                        updatedResource.Tags = resource.Tags;
                        if (resourceDetails.ResourceIdentity.ResourceProviderNamespace == "Microsoft.Web" && resourceDetails.ResourceIdentity.ResourceType == "sites")
                        {
                            // Web sites seem to need the properties set.
                            updatedResource.Properties = resource.Properties;
                        }

                        SetTags(updatedResource, user, time);

                        var result = client.Resources.CreateOrUpdate(resourceDetails.ResourceGroupName, resourceDetails.ResourceIdentity, updatedResource);
                        log.Info("Tags updated!");
                    }
                }
                else
                {
                    log.Info($"Ignoring event from {operationName}");
                }
            }
        }

        private static ResourceDetails GetReourceDetails(ResourceManagementClient client, string resourceId, TraceWriter log)
        {
            var details = new ResourceDetails();

            var bits = resourceId.Split('/');
            details.ResourceGroupName = bits[4];
            string providerName = bits[6];
            var resourceTypeName = bits[7];
            var resourceName = bits[8];

            var resourceProvider = client.Providers.Get(providerName);
            var resourceProviderType = resourceProvider.Provider.ResourceTypes.Where(pt => pt.Name.ToLower() == resourceTypeName.ToLower()).Single();
            var latestApi = resourceProviderType.ApiVersions.First();

            details.ResourceIdentity = new ResourceIdentity(resourceName, providerName + "/" + resourceTypeName, latestApi);
            return details;
        }



        private static void SetTags(GenericResource genericResource, string user, DateTime date)
        {
            if (!genericResource.Tags.ContainsKey("CreatedBy"))
            {
                genericResource.Tags["CreatedBy"] = user;
            }
            if (!genericResource.Tags.ContainsKey("CreatedDate"))
            {
                genericResource.Tags["CreatedDate"] = date.ToString("u");
            }
            genericResource.Tags["ModifiedBy"] = user;
            genericResource.Tags["ModifiedDate"] = date.ToString("u");
        }

        public static string GetAuthorizationHeaderNoPopup()
        {
            var authority = new Uri(new Uri("https://login.windows.net"), tenantId);
            var context = new AuthenticationContext(authority.AbsoluteUri);
            var credential = new ClientCredential(clientId, clientSecret);
            AuthenticationResult result = context.AcquireTokenAsync(azureManagementUrl, credential).Result;
            if (result != null)
                return result.AccessToken;

            throw new InvalidOperationException("Failed to acquire token");
        }

        private class ResourceDetails
        {
            public string ResourceGroupName { get; set; }
            public ResourceIdentity ResourceIdentity { get; set; }

        }
    }
}