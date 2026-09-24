# SMT Order Management

An ASP.NET Core 8 API and web app for Components, versioned Board recipes, stock reservations, Orders, and production handoffs. Azure Table Storage persists the data; local development uses Azurite.

Explore the [web app](https://orange-moss-0c91b0103.2.azurestaticapps.net/) or [Swagger](https://smt-orders-e103ef-api.azurewebsites.net/swagger) with an assigned Entra account. Create Components, assemble a Board recipe, and place an Order to try the full workflow.

## Design

Routes and Entra authentication live in `Program`; Component, Board, and Order services own the domain rules. `MaterialDemand` calculates recipe demand, and `Database` commits changes atomically in one Table partition with ETag checks and retries for concurrent writes.

Available stock is physical stock minus reservations. Board edits create revisions; Orders retain their selected revisions. Reserved Orders can be edited or deleted. The first production download starts the Order, consumes stock, releases reservations, and saves a snapshot in one transaction. Later downloads return the same bytes; Started Orders cannot be edited or deleted.

## Entra registration

1. Create a single tenant API app registration. Expose an Application ID URI such as `api://<api-client-id>` and the delegated scope `access_as_user`. Set `Entra__Audience` to the API token's `aud` value, `Entra__AppIdUri` to the URI, and `Entra__TenantId` to the tenant GUID.
2. Create a separate single tenant SPA registration. Add redirect URIs `http://localhost:8081/auth.html` and `https://<static-app-host>/auth.html` for the web UI, plus `http://localhost:8080/swagger/oauth2-redirect.html` and `https://<api-app>.azurewebsites.net/swagger/oauth2-redirect.html` for Swagger. Give it delegated permission for the API scope and grant admin consent. Set `Entra__BrowserClientId` to its client ID. Both browser interfaces use authorization code with PKCE.
3. Assign the selected users to both the SPA and API enterprise applications, then set **Assignment required** to **Yes** on both. The API itself requires the delegated `access_as_user` scope. All assigned users share the same data and CRUD permissions within each deployment.

For another localhost port, register its redirect URI. See [Microsoft's JWT bearer guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication).

## Run locally with Azurite

Requirements: Docker Compose and a Microsoft Entra tenant. Complete the registration above, copy `.env.example` to `.env`, and fill in your Entra values. Update `web/config.local.json` with the same tenant, browser client, and API scope. `.env` is Git ignored.

```sh
docker compose up --build -d
```

Open the [local web app](http://localhost:8081/) or [local Swagger](http://localhost:8080/swagger) and sign in. Compose runs Azurite on port 10002, the API on 8080, and nginx on 8081; nginx proxies `/api` to the API. `/health` is public; `/api` requires the delegated `access_as_user` scope. The Table is created on startup with no application records, and local data is separate from Azure data.

View API logs with `docker compose logs -f api`. Run the unit tests with the .NET 8 SDK (no storage service required):

```sh
dotnet restore SmtOrders.sln
dotnet build SmtOrders.sln --no-restore
dotnet test SmtOrders.sln --no-build
```

## Run on Azure

The shared demo uses App Service and Table Storage in West Europe, plus Azure Static Web Apps for the UI. Use the web app or Swagger links above to explore; assigned users share the deployment's data and permissions.

### Deploy the API

Complete the [Entra registration](#entra-registration), including your Web App's Swagger redirect URI. Install Azure CLI and the .NET 8 SDK, then run the following from the repository root in PowerShell. Replace the placeholders; Storage and Web App names must be globally unique. Storage names use lowercase letters and digits. Your account needs permission to create resources and assign roles.

These commands create an F1 Windows App Service plan and Standard LRS StorageV2 account. The API accesses Table Storage through its managed identity and creates an empty `SmtOrders` Table on startup.

```powershell
$tenantId = 'YOUR_ENTRA_TENANT_ID'
$subscriptionId = 'YOUR_SUBSCRIPTION_ID'
$resourceGroup = 'YOUR_RESOURCE_GROUP'
$plan = 'YOUR_APP_SERVICE_PLAN_NAME'
$storageAccount = 'YOUR_UNIQUE_LOWERCASE_STORAGE_NAME'
$webApp = 'YOUR_UNIQUE_WEB_APP_NAME'
$apiAudience = 'YOUR_API_TOKEN_AUDIENCE'
$appIdUri = 'api://YOUR_API_CLIENT_ID'
$browserClientId = 'YOUR_BROWSER_CLIENT_ID'

az login --tenant $tenantId
az account set --subscription $subscriptionId
az group create --name $resourceGroup --location westeurope
az appservice plan create --name $plan --resource-group $resourceGroup --location westeurope --sku F1 --is-linux false
az storage account create --name $storageAccount --resource-group $resourceGroup --location westeurope --kind StorageV2 --sku Standard_LRS --allow-blob-public-access false --allow-shared-key-access false --min-tls-version TLS1_2
az webapp create --name $webApp --resource-group $resourceGroup --plan $plan --runtime 'DOTNETCORE:8.0'
az webapp update --name $webApp --resource-group $resourceGroup --https-only true
az webapp config set --name $webApp --resource-group $resourceGroup --use-32bit-worker-process true --ftps-state Disabled --min-tls-version 1.2

$principalId = az webapp identity assign --name $webApp --resource-group $resourceGroup --query principalId --output tsv
$storageId = az storage account show --name $storageAccount --resource-group $resourceGroup --query id --output tsv
az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal --role 'Storage Table Data Contributor' --scope $storageId

az webapp config appsettings set --name $webApp --resource-group $resourceGroup --settings "Storage__TableServiceUri=https://$($storageAccount).table.core.windows.net" 'Storage__TableName=SmtOrders' "Entra__TenantId=$tenantId" "Entra__Audience=$apiAudience" "Entra__AppIdUri=$appIdUri" "Entra__BrowserClientId=$browserClientId"

dotnet publish src/SmtOrders.Api/SmtOrders.Api.csproj --configuration Release --output .scratch/publish
Compress-Archive -Path .scratch/publish/* -DestinationPath .scratch/api.zip -Force
az webapp deploy --name $webApp --resource-group $resourceGroup --src-path .scratch/api.zip --type zip
az webapp log config --name $webApp --resource-group $resourceGroup --application-logging filesystem --level information
```

Allow time for the role assignment to propagate. Open `https://<web-app-name>.azurewebsites.net/swagger`, check `/health`, and sign in to explore the API. Anonymous `/api/components` requests should return `401`. View logs with `az webapp log tail --name $webApp --resource-group $resourceGroup --provider application`.

For CI deployment, set repository variable `AZURE_WEBAPP_NAME` and secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID`. Configure an Entra federated credential for this repository's `main` branch and grant the deployment identity Website Contributor access to the Web App. The [workflow](.github/workflows/ci.yml) builds, tests, and deploys passing `main` commits; see [Azure's OIDC deployment guide](https://learn.microsoft.com/en-us/azure/app-service/deploy-github-actions).

### Deploy the web app

Update `web/config.azure.json` with your API URL and public Entra configuration, and register `https://<static-app-host>/auth.html` as a SPA redirect URI.

When the frontend and API use different origins, configure CORS on the API's Azure App Service so the browser can call it. Using `$resourceGroup` and `$webApp` from the API setup above, replace the placeholder with your frontend's exact origin (scheme and hostname, without a path or trailing slash):

```powershell
az webapp cors add `
  --resource-group $resourceGroup `
  --name $webApp `
  --allowed-origins 'https://YOUR_STATIC_APP_HOST'
```

Use the specific frontend origin rather than `*`. Azure App Service handles CORS for this deployment; the API source does not define a CORS policy. Local Docker Compose needs no CORS configuration because nginx serves the frontend and proxies `/api` through the same origin.

From `web/`, build the static files:

```sh
npm ci
npm run typecheck
npm run build
```

Deploy `web/dist` to Azure Static Web Apps with the [Static Web Apps CLI](https://learn.microsoft.com/en-us/azure/static-web-apps/static-web-apps-cli), keeping its deployment token outside source control. For GitHub Actions, set variable `AZURE_STATIC_WEB_APP_NAME` and secret `AZURE_STATIC_WEB_APPS_API_TOKEN` to enable the workflow's `deploy-web` job.

Remove the resource group when finished to stop storage charges.

## API walkthrough

Use the web app for the workflow and Swagger for endpoint schemas and editable requests:

1. Sign in, create Components with stock, then create a Board recipe using their IDs.
2. Create an Order for a Board revision and observe its stock reservations.
3. Download the production handoff twice: stock is consumed once and both downloads match.
4. Try searches (`?q=text`), Board revisions, and edits to see the domain rules in action.

`PUT` accepts partial objects; omitted fields stay unchanged, while supplied `recipe` or `boards` arrays replace the whole array. Errors use `{ "code": "...", "message": "..." }` with `400` for invalid input, `404` for missing records, and `409` for conflicts.

## Production protocol

Downloads use `application/vnd.smt-production.v1+json` with `schemaVersion` `1.0`. The saved snapshot includes Order and start dates, Board dimensions and build quantities, recipes, and aggregate material demand. It is a planning and kitting handoff; placement coordinates and machine programs are managed elsewhere.

## Limits

One partition serializes writes; catalog scans and JSON rows suit a small demo. Transactions allow at most 100 entity operations, and Boards are capped at 97 revisions (`batch_limit` / `revision_limit`). Existing legacy Tables require a fresh Table because no migration is provided. App Service Free may sleep or reach its quota. Production completion, scrap, replenishment, and line management are outside the API.
