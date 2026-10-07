# Infrastruttura custom di ChaosToDo

Il modello sorgente e' `ChaosToDo.AppHost\AppHost.cs`. Aspire genera i moduli SQL,
Redis, Key Vault e managed identity; quattro template Bicep custom completano
hosting, Automation e Chaos Studio. `aspire-output` e' un risultato del publish,
non la sorgente da modificare ne' l'input di un successivo `aspire deploy`.

Per configurazione e conduzione delle demo vedere [README.md](README.md).

## Mappa dei template

| Template in `ChaosToDo.AppHost\bicep` | Risorse principali | Scopo |
|---|---|---|
| `windows-app-service.bicep` | Plan Windows, sito, app settings, policy publishing, ruolo Key Vault | API per cache e SQL |
| `vm-api.bicep` | VNet/NSG, IP, Load Balancer, VMSS, storage, RBAC | API per Compute Zone Down |
| `sql-failover-automation.bicep` | Automation, runtime, runbook e ruoli custom SQL | Failover HA locale |
| `chaos-studio.bicep` | Workspace, ruoli target, scenari e configurazioni | Orchestrazione dei fault |

Sono registrati con `AddBicepTemplate` solo in publish mode. In `aspire run`
SQL e Redis sono container e l'API e' un progetto locale; questi template
non vengono provisionati.

## Windows App Service

`windows-app-service.bicep` crea un piano **P1v3**, Windows, non zone-redundant,
e un sito .NET 10 con 2 worker per default. `workerCount` ammette 1-3 worker.
Il sito abilita Always On, processo 64-bit, HTTPS-only, TLS minimo 1.2,
HTTP/2 e Health Check `/health`. FTP e autenticazione Basic FTP/SCM sono disabilitati.

La shared user-assigned identity accede a SQL e risolve Redis:

| Configurazione | Origine/utilizzo |
|---|---|
| `ConnectionStrings__database` | SQL FQDN/database e autenticazione Entra |
| `ConnectionStrings__cache` | Key Vault reference versionless al secret Redis |
| `keyVaultReferenceIdentity` | Resource ID della shared identity |
| `AZURE_CLIENT_ID` | Selezione della shared identity |
| `AZURE_TOKEN_CREDENTIALS` | `ManagedIdentityCredential` |
| `HealthChecks__ExposeEndpoints` | Espone `/health` e `/alive` in produzione |
| `Demo__ServedByHeader` | Worker, PID e contatori query per le dashboard |
| `SCM_DO_BUILD_DURING_DEPLOYMENT` | `false`, pacchetto compilato localmente |

Il template assegna `Key Vault Secrets User` sul vault Redis ed esporta
`siteName`, `siteId`, `siteDefaultHostname`, `siteUrl`, `planId`.
Gli URL derivano dal sito reale, non da un dominio ipotizzato.

**Il Bicep non distribuisce il codice.** `WindowsApiPublisher.cs` crea uno ZIP
framework-dependent `win-x64`, con IIS/ANCM OutOfProcess e `ChaosToDo.Api.exe`,
lo carica con `az webapp deploy` usando Entra e controlla `/health` e `/alive`.
Non usa container, ACR o remote build.

## VM scale set e Load Balancer

`vm-api.bicep` crea:

| Risorsa | Configurazione |
|---|---|
| VMSS Uniform | 2 istanze, zone 2/3, zoneBalance, Ubuntu 24.04 Gen2, Trusted Launch, upgrade Manual |
| VM | Default `Standard_D2als_v7`, StandardSSD/NVMe, niente overprovisioning |
| Standard Load Balancer | HTTP 80 -> 8080, probe `/alive` ogni 5 s, outbound rule |
| IP pubblico | Standard statico, zone 1/2/3, DNS dell'endpoint VM |
| VNet/subnet | `10.40.0.0/16`, `10.40.1.0/24`, senza default outbound access |
| NSG | Accesso alla porta 8080 per traffico API/probe, nessun SSH |
| Storage | ZRS, container privato `api-packages`, shared key disabilitata |

Il probe usa **`/alive`**, non `/health`: un errore SQL/Redis non deve togliere
simultaneamente tutte le VM dal pool. L'endpoint demo e' HTTP, non HTTPS.
La password admin e' un parametro Aspire segreto generato e persistito nello
stato locale del deployment; non e' una credenziale in sorgente.

`VmApiPublisher.cs` costruisce il pacchetto self-contained `linux-x64`,
lo carica come `releases/<timestamp>.tar.gz` e `latest.tar.gz`, quindi installa
le release sulle VM una alla volta tramite Run Command.
`vm\chaostodo-api-bootstrap.sh` configura utente e servizio systemd
`chaostodo-api`, variabili SQL/Key Vault, scarica il pacchetto via managed identity
e aggiorna il symlink della release. E' usato da cloud-init e dai redeploy.

Le VM condividono l'identita' dell'API Windows, leggono Redis dal Key Vault
all'avvio e restituiscono `X-Served-By: <host>/zone-<zona>` usando IMDS.
La verifica richiede health e traffico da entrambe le zone.
Il template assegna `Storage Blob Data Reader` alle VM e
`Storage Blob Data Contributor` al deployer per l'upload.

## SQL e Redis generati da Aspire

Le personalizzazioni sono in AppHost, non in template Bicep sorgenti separati.

**SQL:** server con autenticazione Entra e database **BC_Gen5, 2 vCore,
`zoneRedundant: false`**. Business Critical include HA locale, non geo-replication.
`SqlAzureAccessExtensions.cs` aggiunge un deployment script che usa l'identita'
admin SQL per creare/riconciliare l'utente Entra della shared identity e assegnare
`db_owner`, necessario alle migrazioni automatiche della demo.
Il nome fisico del database passa a `AddDatabase`, mentre in locale resta `database`.

**Redis:** Azure Managed Redis (`Microsoft.Cache/redisEnterprise`), Balanced_B0,
autenticazione access key. `WithAccessKeyAuthentication()` genera un Key Vault
RBAC e i secret `connectionstrings--cache` e `primaryaccesskey--cache`.
Le chiavi sono ottenute durante il deployment, non scritte nel codice.
App Service usa una reference al secret; le VM lo leggono con
`Aspire.Azure.Security.KeyVault`. La pipeline non stampa i valori dei secret.

## Automation per SQL HA locale

`sql-failover-automation.bicep` crea Automation Account con identita' dedicata,
local auth disabilitata, runtime **PowerShell 7.4** e runbook `sql-local-ha-failover`.
Il runbook usa HTTP/.NET senza dipendenze dai moduli Az.

L'identita' Automation riceve due ruoli custom:

| Scope | Permessi |
|---|---|
| Solo database target | Lettura database e `Microsoft.Sql/servers/databases/failover/action` |
| Resource Group | Lettura dei risultati operazione SQL per il polling regionale |

`SqlFailoverRunbookPublisher.cs` rende nel contenuto sorgente ID del database
e client ID dell'identita', importa il draft, pubblica e confronta il contenuto.
Il Bicep crea il runbook ma **solo questo passaggio ne pubblica il codice**.
Non viene avviato alcun job durante il deploy.

`runbooks\sql-local-ha-failover.ps1` richiede una sola promozione della primaria
tramite ARM `databases/<db>/failover?replicaType=Primary`.
Attende al massimo 10 minuti il completamento, controlla database Online e
non ritenta il POST in caso di errore ambiguo. Non usa password SQL o T-SQL.
Il polling accetta i percorsi SQL regionali `databaseAzureAsyncOperation`
e `databaseOperationResults`, oltre al percorso database `operationResults`,
vincolando HTTPS, host ARM e scope. Preserva la query firmata senza stamparla.

## Workspace e scenari Chaos Studio

`chaos-studio.bicep` usa API **2026-08-01-preview** e crea
`chaostodo-workspace` in **North Europe**, con scope sul Resource Group.
La regione e' distinta da quella dei target; controllarne il supporto preview
prima di cambiare il template.

La system-assigned identity del workspace riceve Reader sul RG per discovery,
Virtual Machine Contributor sul VMSS, Website Contributor sul sito,
Azure Managed Redis Contributor su Redis e un ruolo custom runbook-runner
sull'Automation Account. L'identita' Automation rimane distinta e non concede
privilegi failover alla shared identity applicativa.

| Scenario | Azione e default |
|---|---|
| `compute-zone-down` | Shutdown VMSS, zona logica 2, `PT2M`, non graceful |
| `cache-stampede` | Flush database Redis |
| `cache-stampede-with-process-crash` | Flush, poi Kill Process con `runAfter` sul successo del flush |
| `sql-local-ha-failover` | StartRunbook su Automation, limite `PT15M` |

I target sono referenziati come `existing`. Ogni scenario ha una configurazione
`default` gestita in IaC. Il Kill Process non imposta `ProcessName` e non garantisce
il restart di tutti i worker. Il failover SQL usa Automation per promuovere una
replica HA locale, anziche' l'azione SQL basata su geo-replication/failover group.

`ChaosWorkspacePublisher.cs` esegue discovery e valutazione, riapplica i default,
poi verifica configurazioni e piani: azioni non saltate, target attesi e parametri
compute. La sola validazione non esegue il codice del runbook e non dimostra che
un fault riuscira'. Nessun passaggio di deploy invoca Run.

## Pipeline e aggiornamenti

La pipeline definita in AppHost ordina provisioning e pubblicazione:
SQL/identity -> Automation/runbook; identity/SQL/Redis/vault -> hosting API;
API healthy e runbook pubblicato -> Chaos -> refresh/validazione.
`WaitFor` governa invece l'avvio locale.

```powershell
# Dalla cartella ChaosToDo.AppHost
aspire deploy --list-steps -e dev
aspire do publish-sql-local-ha-runbook -e dev
aspire do refresh-chaos-workspace -e dev
```

I due ultimi comandi eseguono anche le rispettive dipendenze ma non avviano fault.
I comandi code-only nel README non provisionano infrastruttura ne' aggiornano Chaos.
Per pubblicare runbook servono anche permessi su draft, publish e lettura contenuto.

Nomi stabili e GUID RBAC deterministici rendono i redeploy dichiarativi.
Le modifiche agli scenari dal portale possono essere sovrascritte al deploy.
Cambiare ambiente/prefisso crea nomi diversi, non rinomina risorse esistenti;
rimuovere risorse dal modello non le elimina dal deployment incrementale.

## Limiti e personalizzazione

- Hosting e servizi hanno costi continuativi; SQL Business Critical e' necessario
  al fault HA scelto, non sostituirlo con una SKU senza prima rivedere la demo.
- SQL consente traffico dalle risorse Azure tramite la regola `0.0.0.0`:
  non e' una restrizione alla sola API/RG. SQL e Redis usano rete pubblica.
- `db_owner`, endpoint demo e header diagnostici privilegiano semplicita' e
  osservabilita'; in produzione ridurre permessi e separare migrazioni/runtime.
- Dimensione VM, zone e disponibilita' servizi dipendono dalla subscription/regione.
  Cambiare zone richiede allineare template VM, default Chaos e controlli del publisher.
- I nomi globali derivano da `projectName`/ambiente; rispettare i limiti di naming,
  soprattutto per lo storage (solo lettere minuscole e numeri).
- Workspace/scenari usano API preview: verificare disponibilita' e comportamento
  nella propria subscription. Non rappresentano un outage completo di zona/regione.

Riferimenti: [scenari Chaos Studio](https://learn.microsoft.com/azure/chaos-studio/chaos-studio-scenarios),
[HA Azure SQL](https://learn.microsoft.com/azure/azure-sql/database/high-availability-sla-local-zone-redundancy),
[API SQL Failover](https://learn.microsoft.com/rest/api/sql/databases/failover).
