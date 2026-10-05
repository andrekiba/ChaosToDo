# ChaosToDo

App demo per la sessione "Chaos Engineering su Azure" (Azure AI Day Torino).

Una piccola API Todo in **.NET 10** con **Aspire 13.6.0**, pensata per essere
distribuita su **Azure App Service** (multi-zona) + **Azure SQL Database** + **Azure Managed Redis**,
per preparare una demo di resilienza con Azure Chaos Studio:

1. **Compute Zone Down** — App Service su piano Premium v3 zone-redundant, 2 istanze.
2. **SQL HA locale** — Azure SQL Database Business Critical con repliche HA
   locali, senza ridondanza di zona. Uno scenario custom avvia un runbook Automation
   che richiede un failover coordinato della replica primaria.
3. **Cache Stampede** (+ variante *with Process Crash*) — Azure Managed Redis + App Service, con
   [FusionCache](https://github.com/ZiggyCreatures/FusionCache) davanti a Entity Framework Core per
   mostrare la protezione dallo stampede quando la cache viene svuotata.

Il template pubblica quattro scenari custom compute/cache/SQL. La disponibilità delle Actions
preview va verificata prima della demo: in particolare, la documentazione attuale indica
**App Service Kill Process solo per Windows**, mentre questo progetto usa Linux.

## Struttura della solution

```
ChaosToDo.AppHost/          Progetto Aspire: modella le risorse Azure e orchestra l'app
ChaosToDo.Api/       API minimale (.NET 10) — Todo CRUD, EF Core, FusionCache
ChaosToDo.ServiceDefaults/  Defaults Aspire (OpenTelemetry, health check, resilienza HTTP)
```

## Eseguire in locale

Serve Docker Desktop (o un altro motore di container) avviato: Aspire in modalità `run` esegue
SQL Server e Redis come container locali grazie a `.RunAsContainer()` nell'AppHost, così non serve
una vera subscription Azure per sviluppare.

```bash
cd ChaosToDo.AppHost
aspire run
```

La dashboard di Aspire si apre in automatico e mostra i log/tracce di API, SQL e Redis. L'endpoint
dell'API espone:

| Endpoint | Descrizione |
|---|---|
| `GET /api/todos` | Lista, **passa da FusionCache** (L1 in memoria + L2 Redis) |
| `GET /api/todos/nocache` | Stessa query, **senza cache** — usalo per mostrare il "prima" |
| `GET /api/todos/{id}` | Singolo item, cache-aside via FusionCache |
| `POST /api/todos` | Crea un item (`{ "title": "..." }`) e invalida la cache |
| `PUT /api/todos/{id}` | Aggiorna e invalida la cache |
| `DELETE /api/todos/{id}` | Cancella e invalida la cache |

Ogni chiamata "fredda" (factory di FusionCache o `/nocache`) ha una latenza simulata di 800&nbsp;ms
(`Demo:SimulatedDbLatencyMs` in `appsettings.json`) apposta per rendere visibile a occhio, o con un
tool di carico, la differenza fra una richiesta che va al database e una servita dalla cache.

## Pubblicare su Azure

```bash
cd ChaosToDo.AppHost
aspire publish -e dev   # genera gli artefatti Bicep per l'ambiente dev
aspire deploy -e dev    # provisiona le risorse Azure e pubblica l'API per dev
```

`aspire deploy` chiede l'accesso alla subscription Azure (via `az login` / Azure Developer CLI) e
crea: il resource group, il piano App Service Premium v3 zone-redundant, il sito App Service con
l'API, il server + database Azure SQL Business Critical non zone-redundant, e la cache Azure Managed
Redis. La configurazione Azure può essere fornita tramite configurazione o richieste
interattive per i valori mancanti.

`-e dev` (equivalente a `--environment dev`) imposta l'ambiente dell'AppHost a `dev`
e seleziona la configurazione `appsettings.dev.json`. Nel repository questo file
configura la regione `italynorth` e il Resource Group `chaos-todo-dev-rg`, salvo
override di configurazione. I nomi delle risorse personalizzati in `AppHost.cs`
usano `builder.Environment.EnvironmentName` e diventano quindi:

| Risorsa | Nome generato |
|---|---|
| App Service Plan | `chaos-todo-dev-plan` |
| Web App API | `chaos-todo-dev-api` |
| Server SQL | `chaos-todo-dev-sql` |
| Database SQL | `chaos-todo-dev-sqldb` |
| Managed Redis | `chaos-todo-dev-redis` |
| Key Vault Redis | `chaos-todo-dev-kv` |
| Container Registry | `chaostododevacr` |
| Dashboard Aspire | `chaos-todo-dev-dashboard` |
| Identità dashboard | `chaos-todo-dev-dashboard-mi` |
| Identità applicativa | `chaos-todo-dev-mi` |
| Identità amministrativa SQL | `chaos-todo-dev-sql-admin-mi` |

Key Vault, ACR e dashboard non usano hash o suffissi automatici. ACR non ammette
trattini: il nome compatto usa l'ambiente in minuscolo, quindi `dev` e `Dev`
non distinguono due registry. I nomi devono essere globalmente disponibili:
se sono occupati, occorre scegliere esplicitamente un altro nome, non viene
generato un fallback. In publish mode l'AppHost controlla i vincoli sintattici e
di lunghezza: vault massimo 24 caratteri, registry 5-50 caratteri alfanumerici,
sito dashboard massimo 60 caratteri. La disponibilità globale è un controllo Azure
separato; il publish non la garantisce.

Usare `-e dev` anche per il deploy: senza questa opzione, publish e deploy usano
`Production` per default. Il publish non cambia il default del comando successivo.
Cambiare ambiente cambia questi nomi e può creare risorse distinte al deployment,
non rinomina automaticamente quelle già distribuite.

L'ambiente dell'AppHost `dev` è distinto dalla risorsa di hosting chiamata
`app-service-env` in `AddAzureAppServiceEnvironment("app-service-env")`.
Quest'ultimo nome rimane invariato nei moduli Bicep e nell'app setting
`ASPIRE_ENVIRONMENT_NAME`, che nell'output vale ancora `app-service-env`.
`chaostodo-workspace` non acquisisce automaticamente il suffisso `dev`;
ACR, Key Vault e identità hanno invece nomi personalizzati nell'AppHost.
La regione del workspace Chaos Studio rimane `northeurope`.

### Risorse già distribuite e cambio di nome

Rinominare nel modello una risorsa Azure non la rinomina in-place: il prossimo
deploy crea la risorsa con il nuovo nome. Il deployment incrementale non elimina
automaticamente quelle precedenti. Nell'inventario del Resource Group
`chaos-todo-dev-rg` effettuato prima di queste modifiche erano presenti:

| Risorsa precedente | Nuova risorsa prevista |
|---|---|
| `cachekv-yeh5i4ns3abhg` | `chaos-todo-dev-kv` |
| `appserviceenvacryeh5i4ns3abhg` | `chaostododevacr` |
| `app-service-env-aspiredashboard-yeh5i4ns3abhg` | `chaos-todo-dev-dashboard` |
| `app_service_env_contributor_mi-yeh5i4ns3abhg` | `chaos-todo-dev-dashboard-mi` |

Le vecchie risorse restano intatte e possono continuare a generare costi.
La loro eventuale rimozione, insieme ai ruoli obsoleti, richiede una pulizia separata,
dopo aver verificato che la nuova configurazione funzioni e non abbia più riferimenti
alle risorse precedenti. Non eliminare vault, immagini o identità per liberare nomi
senza verificare i consumatori.

La nuova identità dashboard avrà nuovi client/principal ID, perché è una nuova
risorsa. La shared identity e l'identità SQL admin mantengono invece i loro nomi.
La pipeline deve riscrivere i secret Redis nel nuovo vault e pubblicare l'immagine
nel nuovo registry: non serve copiare manualmente credenziali. Il nome della
dashboard e l'output `AZURE_APP_SERVICE_DASHBOARD_URI` sono personalizzati insieme,
così sidecar OTLP e riepilogo deploy puntano al nuovo hostname.

In quell'inventario il server SQL era presente ma il database applicativo no:
Azure aveva rifiutato `zoneRedundant: true` con `ProvisioningDisabled`.
Non risultavano ancora creati API, workspace o scenari Chaos.

> Nota: in questo ambiente sandbox non è stato possibile eseguire `aspire run`/`aspire deploy` fino
> in fondo per una limitazione di rete locale del container (il canale di comunicazione interno fra
> CLI e AppHost non riesce ad aprire un socket) — non è legata al codice. Sul tuo PC, con Docker
> attivo, dovrebbe funzionare direttamente; se incontri un problema diverso, dimmi l'errore esatto e
> lo sistemiamo.

## File Bicep generati da `aspire publish`

La cartella `ChaosToDo.AppHost/aspire-output/` contiene gli artefatti generati dal modello
di `AppHost.cs`. La descrizione seguente si riferisce all'output esaminato con Aspire
**13.6.0**, dopo `aspire publish -e dev`, in ambiente **dev**: nomi, proprietà e
file possono cambiare dopo un nuovo publish o una modifica dell'AppHost.

Le sorgenti da mantenere sono `ChaosToDo.AppHost/AppHost.cs`, i template custom
`ChaosToDo.AppHost/bicep/chaos-studio.bicep` e
`ChaosToDo.AppHost/bicep/sql-failover-automation.bicep`, oltre al contenuto del runbook
in `ChaosToDo.AppHost/runbooks/`. Modificare solo i file in `aspire-output` non modifica
il modello Aspire e un successivo publish può sovrascrivere le modifiche.
`aspire deploy` esegue il deployment dal modello AppHost, non usa questa cartella come
input di un deployment precedentemente pubblicato.

### Architettura e convenzioni

L'output descrive un deployment su Azure App Service, non un deployment del processo
AppHost su Azure:

```text
App Service Plan
|-- Web App API
|   |-- immagine da Azure Container Registry
|   |-- managed identity -> Azure SQL
|   |-- managed identity -> Key Vault -> credenziali Redis
|   `-- sidecar OpenTelemetry -> dashboard Aspire
`-- Dashboard Aspire

Chaos Studio Workspace
`-- scenari che agiscono su API, Redis e SQL
```

| Nel modello Aspire | Negli artefatti Azure |
|---|---|
| `AddAzureSqlServer` e `AddDatabase` | Server SQL, database e identità amministrativa |
| `AddAzureManagedRedis` con access key | Redis, Key Vault e secret |
| `AddAzureAppServiceEnvironment` | App Service Plan, ACR, identità e dashboard |
| `AddProject(...).WithReference(...)` | Web App, impostazioni, identità e autorizzazioni |
| `AddBicepTemplate("chaos-studio", ...)` | Template Chaos Studio personalizzato |

L'applicazione usa una sola user-assigned managed identity, `sharedIdentity`
(nome logico Aspire `identity`, nome Azure `chaos-todo-dev-mi`). Rimangono separate
l'identità amministrativa SQL, l'identità della dashboard, quella del Workspace
e la user-assigned identity dell'Automation Account.

| Identità | Permessi e utilizzo |
|---|---|
| `chaos-todo-dev-mi` | `db_owner` nel database SQL, `Key Vault Secrets User` sul vault Redis, `AcrPull` sul registry e autenticazione del sidecar OTLP verso la dashboard |
| `chaos-todo-dev-sql-admin-mi` | Amministratore Entra del server SQL; esegue lo script che concede accesso al database |
| `chaos-todo-dev-dashboard-mi` | Reader sul Resource Group e Website Contributor sul sito API per la dashboard |
| Identità system-assigned di `chaostodo-workspace` | Ruoli sui target necessari per le azioni Chaos Studio |
| `chaos-todo-dev-sql-failover-mi` | Lettura e richiesta failover sul solo database SQL target; lettura dei risultati operazione SQL nel Resource Group |

Il pull delle immagini usa la stessa identità dell'API mediante
`WithAcrPullIdentity(sharedIdentity)` sull'ambiente e
`WithAzureUserAssignedIdentity(sharedIdentity)` sul progetto. Il push delle immagini
rimane responsabilità della credenziale di deployment: l'app non riceve `AcrPush`,
Contributor o Owner sul Resource Group. Redis mantiene l'autenticazione con access key.

Rispetto al publish precedente, `identity/identity.bicep` sostituisce
`api-identity/api-identity.bicep`; l'identità ACR automatica non viene più creata
in `app-service-env.bicep`. Il ruolo AcrPull è ora nel modulo
`identity-roles-app-service-env-acr/identity-roles-app-service-env-acr.bicep`.
Il modulo `api-roles-sql` non viene più generato: lo script SQL custom è integrato
in `sql/sql.bicep`. Il publish include anche i moduli Automation e identità SQL failover; il runbook
è caricato e pubblicato dal passaggio Aspire dedicato, non dal solo `main.bicep`.

In Bicep, `resource` crea o aggiorna una risorsa, mentre `existing` la referenzia senza
crearla. `module` distribuisce un altro template e `output` espone valori ai moduli
chiamanti o alla pipeline.

`uniqueString(resourceGroup().id)` produce un suffisso deterministico, non un valore
casuale a ogni deployment; resta nell'output tecnico `webSiteSuffix`, ma non nei nomi
personalizzati elencati sopra. `guid(...)` produce identificatori stabili per le assegnazioni
RBAC. La data dopo `@`, per esempio
`@2025-03-01`, è la versione dell'API Azure Resource Manager, non quella di Aspire o
del container.

I nomi `chaos-todo-dev-*` derivano dall'ambiente dell'AppHost al momento del
publish. Negli artefatti esaminati sono stringhe fisse, non parametri modificabili
durante il deployment Bicep.

### 1. `main.bicep`: coordinamento dell'infrastruttura

È il template principale e l'unico con `targetScope = 'subscription'`: può creare il
Resource Group e distribuire al suo interno gli altri moduli.

| Input | Utilizzo |
|---|---|
| `resourceGroupName` | Nome del Resource Group |
| `location` | Regione del Resource Group e della maggior parte delle risorse |
| `principalId` | Passato a `app-service-env` come `userPrincipalId`, attualmente inutilizzato in quel modulo |

Include SQL, Redis, Key Vault, ACR, ambiente App Service, Chaos Studio, identità
condivisa e assegnazioni RBAC verso Key Vault e ACR. SQL riceve nome e client ID
da `identity.outputs`; l'ambiente riceve ID e client ID della stessa identità.

Le dipendenze sono in gran parte implicite: passare `cache_kv.outputs.name` al modulo
Redis lo fa attendere fino alla disponibilità dell'output del Key Vault. L'ordine
testuale dei moduli non stabilisce l'ordine di esecuzione.

Esporta endpoint ACR, ID del piano, identità, hostname SQL e Redis, nome e URI del
Key Vault e URL della dashboard.

**Non include `api/api.bicep`** e non richiede immagine o porta del container.
Infrastruttura e applicazione sono artefatti separati: distribuire soltanto `main.bicep`
non distribuisce anche il sito dell'API.

### 2. `sql/sql.bicep`: Azure SQL

Crea un'identità amministrativa, un server, una regola firewall, un database e
lo script che autorizza l'identità condivisa.

La user-assigned managed identity `chaos-todo-dev-sql-admin-mi` diventa l'amministratore
Microsoft Entra del server. È utilizzata dallo script che autorizza l'identità API nel
database, ma non è l'identità con cui l'API lavora normalmente.

Il server `chaos-todo-dev-sql` configura:

- autenticazione solo Microsoft Entra (`azureADOnlyAuthentication: true`);
- TLS minimo 1.2;
- accesso di rete pubblico abilitato;
- amministratore impostato sull'identità precedente.

Non vengono configurate credenziali SQL username/password.

La regola firewall `AllowAllAzureIps`, con intervallo `0.0.0.0`-`0.0.0.0`, è la
convenzione Azure SQL per consentire connessioni dalle risorse Azure. Non limita
l'accesso alla sola API o al solo Resource Group; autenticazione e autorizzazione
restano comunque necessarie.

Il database fisico `chaos-todo-dev-sqldb` usa `BC_Gen5`, tier `BusinessCritical`,
family `Gen5`, capacity `2` vCore e `zoneRedundant: false`. Business Critical conserva
le repliche HA locali anche senza distribuire le repliche tra availability zone.
Questo non fornisce protezione SQL dalla perdita di un'intera zona.

La ridondanza di zona SQL è stata disabilitata dopo il rifiuto Azure
`ProvisioningDisabled: Provisioning of zone redundant database/pool is not supported
for your current request`. Questo messaggio non identifica da solo una limitazione
regionale, di capacità o della sottoscrizione. Il successivo deploy deve ancora
confermare che la configurazione non zone-redundant venga accettata.

Il free limit è disabilitato (`useFreeLimit: false`). Rimane la proprietà generata
`freeLimitExhaustionBehavior: 'AutoPause'`, ma l'offerta gratuita non è abilitata.

**Accesso applicativo SQL.** L'helper `WithManagedIdentityDatabaseAccess(sharedIdentity)`
in `SqlAzureAccessExtensions.cs` aggiunge `script_grant_mi_database`, una risorsa
`Microsoft.Resources/deploymentScripts`, nello stesso modulo. Le assegnazioni SQL
automatiche per progetto sono disabilitate con `ClearDefaultRoleAssignments()`.

Lo script PowerShell viene eseguito con l'identità amministrativa SQL, ottiene un
token Entra, apre una connessione cifrata e crea o riconcilia un utente esterno per
`sharedIdentity`. Il SID viene derivato dal client ID e l'utente riceve `db_owner`,
necessario per le migrazioni `Database.MigrateAsync()` all'avvio della demo ma più
ampio dei permessi richiesti per il solo CRUD.

Il T-SQL è idempotente e transazionale: conserva l'utente se il SID coincide e
ricrea un utente Entra omonimo con SID differente. Usa `System.Data.SqlClient`,
fino a cinque tentativi con intervallo di 60 secondi e retention di un'ora.
Lo script ha una dipendenza esplicita dal database.

`DBNAME` ora vale `chaos-todo-dev-sqldb`, come la connection string dell'API.
Il nome fisico viene impostato tramite `AddDatabase("database", databaseName: ...)`,
non soltanto rinominando la risorsa Bicep. In locale resta `database`, così il
container SQL persistente e i suoi dati continuano a essere utilizzati.

Gli output espongono FQDN, nome e ID del server e nome dell'identità amministrativa.

### 3. `cache-kv/cache-kv.bicep`: Key Vault per Redis

Crea il Key Vault Standard `chaos-todo-dev-kv`, con
`enableRbacAuthorization: true`: l'accesso ai secret è governato da Azure RBAC, non
dalle access policy legacy.

Questo modulo crea soltanto il vault. I secret sono scritti da `cache.bicep` e i
permessi di lettura sono assegnati da `api-roles-cache-kv.bicep`.

Espone nome, resource ID e URI del vault.
L'AppHost personalizza il vault generato da `WithAccessKeyAuthentication()`,
senza aggiungerne un secondo: resta valida anche la rimozione automatica dal
modello locale quando Redis viene eseguito come container.

### 4. `cache/cache.bicep`: Azure Managed Redis e secret

Crea `chaos-todo-dev-redis` di tipo `Microsoft.Cache/redisEnterprise`.
È Azure Managed Redis, non la risorsa classica `Microsoft.Cache/Redis`.

Configura SKU `Balanced_B0`, TLS minimo 1.2 e accesso pubblico abilitato. Il database
figlio `default` usa la porta `10000` e abilita l'autenticazione tramite access key.

Il Key Vault viene referenziato come `existing`. Nel vault vengono creati:

| Secret | Contenuto |
|---|---|
| `connectionstrings--cache` | Connection string Redis con TLS e credenziali |
| `primaryaccesskey--cache` | Primary key ottenuta con `cache_default.listKeys().primaryKey` |

La chiave viene recuperata da Azure durante il deployment, non è una password
hardcoded nell'AppHost. Il modulo dipende dal Key Vault e restituisce nome, ID e
hostname Redis.

### 5. `app-service-env-acr/app-service-env-acr.bicep`: Container Registry

Crea l'Azure Container Registry `chaostododevacr` con SKU `Basic`.
Espone nome, login server e resource ID.

Il registry ospita l'immagine Docker dell'API. Questo Bicep crea il registry, ma
non costruisce né carica l'immagine: sono operazioni della pipeline applicativa.

### 6. `app-service-env/app-service-env.bicep`: piano, identità e dashboard

Configura l'ambiente di hosting condiviso.

**Identità ACR.** Riceve ID e client ID di `sharedIdentity` come parametri e li
espone negli output ACR. Non crea più `app_service_env_mi` né il relativo ruolo.
L'assegnazione AcrPull è nel modulo dedicato descritto sotto. Il client ID condiviso
è anche inserito in `ALLOWED_MANAGED_IDENTITIES` per la telemetria, senza sostituire
l'identità propria della dashboard.

**App Service Plan.** Crea `chaos-todo-dev-plan`:

| Proprietà | Valore |
|---|---|
| Sistema operativo | Linux |
| SKU | `P1v3`, PremiumV3 |
| Capacità del piano | 2 worker |
| Zone redundancy | Abilitata |
| `perSiteScaling` | Abilitato |

È la capacità di calcolo condivisa da API e dashboard. La configurazione deriva
dalla personalizzazione in `AppHost.cs` per la demo di resilienza alle zone.

**Identità dashboard.** Crea `chaos-todo-dev-dashboard-mi`.
Il suo identificatore Bicep resta `app_service_env_contributor_mi`, ma non è
un'identità applicativa aggiuntiva. Qui riceve il ruolo **Reader** sul Resource
Group. Il ruolo Website Contributor sul sito API è assegnato separatamente da `api.bicep`.

**Dashboard.** Crea `chaos-todo-dev-dashboard`, un sito dedicato sullo stesso piano, con
`linuxFxVersion: 'ASPIREDASHBOARD|1.0'` e `kind: 'app,linux,aspiredashboard'`.
È una dashboard ospitata su Azure, non il processo AppHost distribuito.

Il sito usa un worker, `alwaysOn: true`, HTTP/2, porta web `5000` e porta HTTP/2
dedicata `4317`. L'importazione manuale dei dati è disabilitata.

Gli auth mode interni frontend, OTLP e resource service client sono `Unsecured`.
Il template configura anche `ALLOWED_MANAGED_IDENTITIES`: l'autenticazione interna
della dashboard va distinta dall'integrazione di accesso della piattaforma App Service.
Non è il meccanismo con token di login della dashboard locale.

Gli output espongono piano, registry, identità e URL HTTPS della dashboard
`https://chaos-todo-dev-dashboard.azurewebsites.net`. La personalizzazione modifica
sia il sito sia l'output URI, che viene consumato anche dalla configurazione OTLP dell'API.

### 7. `identity/identity.bicep`: identità applicativa condivisa

Crea la user-assigned managed identity `chaos-todo-dev-mi`, usata dall'API per
Azure SQL, Key Vault, pull ACR e autenticazione della telemetria. Non crea una
seconda identità per il pull. La risorsa viene dichiarata prima dei consumatori;
il wiring dei permessi applicativi Azure avviene solo in publish mode.

| Output | Significato |
|---|---|
| `id` | Resource ID ARM dell'identità |
| `clientId` | Identificatore usato per selezionarla nelle credenziali |
| `principalId` | Object ID del service principal, usato per RBAC |
| `principalName`, `name` | Nome della risorsa |

### 8. `api-roles-cache-kv/api-roles-cache-kv.bicep`: accesso ai secret

Referenzia il Key Vault esistente e assegna a `sharedIdentity` il ruolo
**Key Vault Secrets User** a livello dell'intero vault. Consente di leggere i valori
dei secret, non di amministrare il vault.

Rende utilizzabili i riferimenti App Service `@Microsoft.KeyVault(SecretUri=...)`.
La Web App seleziona l'identità mediante `keyVaultReferenceIdentity`.

Il nome del modulo conserva il prefisso `api` perché nasce dalla reference
dell'API alla cache; il `principalId` passato da `main.bicep` è però
`identity.outputs.principalId`, non quello di un'identità API separata.

### 9. `identity-roles-app-service-env-acr/identity-roles-app-service-env-acr.bicep`: pull ACR

Referenzia l'ACR esistente e assegna **AcrPull** a
`identity.outputs.principalId`, con scope limitato al registry.
Il ruolo viene dichiarato esplicitamente perché `WithAcrPullIdentity` sostituisce
l'identità automatica ma non concede da solo l'autorizzazione.
`main.bicep` passa nome ACR e principal ID; la dipendenza da entrambi i moduli
deriva dai loro output. Non viene concesso il permesso di push.

### 10. `api/api.bicep`: sito API e configurazione runtime

Riceve gli output dell'infrastruttura e i parametri applicativi
`api_containerimage` e `api_containerport`.

Crea `chaos-todo-dev-api` sul piano condiviso, con
`linuxFxVersion: 'SITECONTAINERS'`. La risorsa figlia
`Microsoft.Web/sites/sitecontainers`, chiamata `main`, definisce immagine, porta,
container principale e autenticazione ACR tramite managed identity.

La Web App associa soltanto `sharedIdentity`. La personalizzazione del sito
rimuove il doppio riferimento che Aspire aggiunge per i percorsi app e ACR:
entrambi risolvono alla stessa risorsa, ma nel dizionario
`userAssignedIdentities` viene emessa una sola voce.

**SQL.** La connection string usa `Encrypt=True`,
`Authentication="Active Directory Default"` e `Database=chaos-todo-dev-sqldb`.
`AZURE_CLIENT_ID` seleziona l'identità applicativa e `AZURE_TOKEN_CREDENTIALS`
indica `ManagedIdentityCredential`.

`ConnectionStrings__database` è la chiave logica corretta per
`AddSqlServerDbContext("database")`. Il nome logico della connessione resta
`database`, mentre il catalogo fisico è `chaos-todo-dev-sqldb`, coerente con
il database creato e lo script SQL.

**Redis.** L'API usa soltanto `ConnectionStrings__cache`, pubblicata come riferimento
Key Vault e risolta a runtime da App Service con `sharedIdentity`.
In publish mode l'AppHost rimuove le proprietà generiche `CACHE_HOST`, `CACHE_PORT`,
`CACHE_PASSWORD` e `CACHE_URI` aggiunte automaticamente da `.WithReference(cache)`,
perché il codice dell'API non le consuma. In locale la reference resta invariata.

In particolare, comporre `CACHE_URI` con la password richiederebbe ad Aspire di
leggere `primaryaccesskey--cache` durante il deploy, usando la credenziale del
deployer. Rimuovendo quella variabile non viene più generato il parametro
`cache_kv_secrets_primaryaccesskey__cache` nel modulo API e non serve concedere
al deployer accesso ai valori dei secret per questo percorso. Il ruolo
Key Vault Secrets User di `sharedIdentity` resta necessario per la risoluzione
della connection string a runtime.

**Telemetria.** Il percorso è:

```text
API -> OTLP/gRPC su localhost:6001 -> sidecar OpenTelemetry -> dashboard Azure
```

Lo abilitano `WEBSITE_ENABLE_ASPIRE_OTEL_SIDECAR=true`,
`OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:6001` e
`OTEL_COLLECTOR_URL=<URL dashboard>`. L'HTTP su localhost è un collegamento interno
verso il sidecar, non la connessione pubblica HTTPS alla dashboard.
`OTEL_CLIENT_ID` e i parametri dell'identità ACR risolvono al client ID di
`sharedIdentity`, lo stesso usato da `AZURE_CLIENT_ID`.

Il modulo assegna all'identità dashboard **Website Contributor** sul solo sito API.
Configura inoltre `OTEL_SERVICE_NAME` come app setting "sticky" per gli slot,
senza creare uno slot di deployment.

Il sito dichiara `numberOfWorkers: 30`, mentre il piano ha capacità `2`.
Sono proprietà di livelli diversi: il template non crea un piano con 30 VM,
ma questa combinazione va verificata prima del deployment.

### 11. `chaos-studio/chaos-studio.bicep`: workspace e scenari

È la copia del template custom `ChaosToDo.AppHost/bicep/chaos-studio.bicep`,
incluso da `AddBicepTemplate` solo in publish mode.

Crea `chaostodo-workspace` in **North Europe**, indipendentemente dalla `location`
del deployment. Il template usa API preview per workspace e scenari; i commenti
motivano la regione fissa con la disponibilità regionale di quel modello.

Il workspace ha una system-assigned managed identity e il Resource Group come scope.
Referenzia API, Redis e Automation Account come `existing` e assegna:

| Target | Ruolo dell'identità workspace |
|---|---|
| Web App API | Website Contributor |
| Managed Redis | Azure Managed Redis Contributor |
| Automation Account | Custom role `runbook-runner`: lettura RBAC e runbook, lettura/creazione job, lettura stream e stop/suspend dei job, come richiesto dall'Action StartRunbook |

Sono autorizzazioni per effettuare le azioni di fault, non per leggere i dati applicativi.
La persona che avvia lo Scenario deve inoltre avere Contributor sul Workspace
(o il permesso custom `Microsoft.Chaos/workspaces/scenarios/run/action`).

| Scenario | Azione dichiarata |
|---|---|
| `compute-zone-down` | Kill del processo App Service nelle istanze della zona richiesta, durata `PT2M` |
| `cache-stampede` | Flush Redis, durata `PT2M` |
| `cache-stampede-with-process-crash` | Flush Redis e kill del processo App Service, entrambe `PT2M` |
| `sql-local-ha-failover` | StartRunbook sul solo Automation Account, durata massima `PT15M`; il runbook attende l'LRO per al massimo 10 minuti |

`compute-zone-down` non spegne una availability zone Azure: simula l'impatto
uccidendo i processi nelle istanze della zona selezionata. L'ultimo scenario dichiara
entrambe le azioni; la semantica temporale effettiva dipende dal motore Chaos Studio.
Distribuire il template crea gli scenari, ma non li avvia.

**Failover SQL locale.** Lo scenario `sql-local-ha-failover` usa
`urn:csci:microsoft:automation:startrunbook/1.0.0` con i parametri
`RunbookName` e `RunbookParameters` (`{}`). L'azione ha un limite `PT15M`;
il limite protegge il tempo di esecuzione, non sostituisce il cooldown SQL.
Il catalogo Actions della preview indica che
`urn:csci:microsoft:sql:failover/1.0.0` invoca `replicationLinks/failover` o
`failoverGroups/failover`: richiede geo-replication o un failover group,
non usa le repliche HA locali di Business Critical. Il runbook usa invece l'API
SQL `databases/{name}/failover` ed è bloccato sul database esatto. La sua identity ha `Microsoft.Sql/servers/databases/read` e
`Microsoft.Sql/servers/databases/failover/action` sul database. Perché l'header
ARM `Location` del polling punta a un'operazione SQL regionale sotto il Resource
Group, `Microsoft.Sql/servers/databases/operationResults/read` è assegnato a
quel Resource Group: consente di leggere lo stato delle operazioni database lì
presenti, ma non di leggerne o modificarne i dati. Non riceve Contributor.
Il deployer che esegue il passaggio di pubblicazione deve disporre di
`Microsoft.Automation/automationAccounts/runbooks/draft/content/write`,
`runbooks/publish/action`, `runbooks/draft/read` e `runbooks/content/read`
sull'Automation Account.

Vedi [catalogo scenari Chaos Studio](https://learn.microsoft.com/azure/chaos-studio/chaos-studio-scenarios),
[HA locale e ridondanza di zona SQL](https://learn.microsoft.com/azure/azure-sql/database/high-availability-sla-local-zone-redundancy)
e [API SQL Failover](https://learn.microsoft.com/rest/api/sql/databases/failover).
Rimuovere uno scenario dal template non elimina eventuali copie già create
da un deployment precedente.

**Ordine di deployment Aspire.** `AppHost.cs` configura la pipeline affinché
`provision-sql-failover-automation` dipenda dal SQL e dall'identità dedicata;
il passaggio di publish del runbook dipende dal completamento di tale provisioning;
`provision-chaos-studio` dipende poi dal runbook pubblicato, dal sito API, da SQL
e da Redis. Il workspace, gli scenari e le assegnazioni RBAC vengono quindi
distribuiti soltanto dopo il provisioning dei target. La dipendenza dal sito viene
risolta tramite il deployment target App Service, senza confonderla con
`.WaitFor(...)`, che governa l'esecuzione locale.

Questa dipendenza appartiene alla pipeline Aspire: non aggiunge un `dependsOn`
al `main.bicep` pubblicato. Chi applica manualmente i Bicep deve rispettare lo
stesso ordine; `main.bicep` da solo non include il sito API.

Per vedere il grafo completo senza distribuire risorse:

```bash
cd ChaosToDo.AppHost
aspire do diagnostics -e dev
```

`--list-steps` non esegue la fase `BeforeStart` che materializza il sito App Service,
quindi non mostra tutte le dipendenze del grafo effettivo.

**Redeploy e idempotenza.** Workspace e scenari hanno nomi stabili e le assegnazioni
RBAC hanno GUID deterministici. Ripetere `aspire deploy -e dev` nello stesso
Resource Group mantiene le stesse risorse, senza crearne copie. L'identità
system-assigned resta associata allo stesso workspace durante un normale
aggiornamento; eliminare e ricreare il workspace genera invece una nuova identità.

Il comportamento è dichiarativo, non "crea una volta e non toccare più":
se cambia il template, il deployment può aggiornare gli scenari e le proprietà
gestite. Le modifiche effettuate dal portale possono essere sovrascritte quando
il template viene riapplicato. Non è garantita l'assenza di richieste ARM su un
redeploy invariato: Aspire può usare la cache di deployment e Azure può
riconciliare la configurazione esistente. Gli scenari non vengono avviati dal deploy.

Cambiare Resource Group cambia lo scope delle risorse. Il nome del workspace
`chaostodo-workspace` non contiene l'ambiente: ambienti diversi distribuiti nello
stesso Resource Group condividerebbero quel workspace e i nomi degli scenari.
Per isolarli, usare Resource Group distinti.

### Punti da verificare prima del deployment

L'output esaminato presenta aspetti concreti da risolvere o verificare nel modello sorgente:

Il nuovo publish con identità condivisa allinea nome fisico SQL, script e
connection string e mantiene il free limit disabilitato. Restano separati
dal refactoring delle identità i seguenti punti:

| Punto | Impatto |
|---|---|
| API separata da `main.bicep` | Il deployment del solo template principale non distribuisce l'applicazione |
| Deployment manuale dei Bicep | L'ordine dei target prima di Chaos Studio è gestito dalla pipeline Aspire, non da `main.bicep` |
| API con `numberOfWorkers: 30` e piano con capacità 2 | Verificare la configurazione effettiva delle istanze |
| App Service Kill Process su Linux | La documentazione preview indica supporto solo Windows; i due scenari con kill non sono validati sul piano Linux |
| Catalogo Actions in North Europe | La richiesta al catalogo ha restituito `NoRegisteredProviderFound`, mentre East US 2 ha risposto; verificare separatamente la disponibilità di workspace e Actions prima di cambiare regione |
| SQL non zone-redundant | Il publish genera la configurazione corretta, ma non dimostra che Azure accetti il provisioning Business Critical |

Queste osservazioni descrivono i file pubblicati, non dimostrano che il deployment Azure
sia stato completato con successo.

## Collegare la demo agli Scenari di Chaos Studio

- **Compute Zone Down**: lo Scenario custom punta al sito API sul piano App Service
  zone-redundant con 2 istanze. Non spegne una zona Azure: esegue il kill del processo
  sulle istanze nella zona selezionata, simulando l'impatto sul servizio.
- **SQL HA locale**: lo Scenario custom `sql-local-ha-failover` avvia il runbook
  Azure Automation pubblicato per richiedere il cambio della primaria HA locale.
- **Cache Stampede**: punta lo Scenario alla cache `cache` (Azure Managed Redis) e all'App Service.
  Prima della demo, genera un po' di carico su `GET /api/todos` (es. con `hey` o `bombardier`) per
  scaldare la cache, poi lascia partire lo Scenario: mostra come con FusionCache le richieste
  concorrenti collassano su un'unica chiamata al database invece di travolgerlo. Per il "prima",
  ripeti lo stesso carico su `GET /api/todos/nocache`.

## Failover SQL: obiettivo della demo e limiti

**Implementato nell'AppHost:** il database `chaos-todo-dev-sqldb` è Business Critical
Gen5, 2 vCore, con `zoneRedundant: false`. L'HA locale è già fornita dal servizio:
non vengono create repliche aggiuntive nell'AppHost. Il modulo
`ChaosToDo.AppHost/bicep/sql-failover-automation.bicep` crea l'Automation Account,
il runtime PowerShell 7.4, il runbook e i ruoli minimi; il modulo
`ChaosToDo.AppHost/bicep/chaos-studio.bicep` crea lo Scenario custom.
Durante `aspire deploy`, il passaggio `publish-sql-local-ha-runbook` importa
il contenuto di `ChaosToDo.AppHost/runbooks/sql-local-ha-failover.ps1`, lo pubblica
e verifica il contenuto draft e quello pubblicato. Questo passaggio non avvia un job.

### Tre livelli di protezione distinti

HA significa *High Availability*, cioè alta disponibilità. In questo contesto
"locale" significa all'interno dell'infrastruttura Azure nella stessa regione,
non sul computer dello sviluppatore.

| Caso | Distribuzione e protezione | Configurazione della demo | Cosa copre il test previsto |
|---|---|---|---|
| **1. HA locale** | Repliche HA locali, senza garanzia di separazione tra zone; protegge da guasti di nodo, processo, hardware e manutenzione | **Presente** con Business Critical e `zoneRedundant: false` | Recupero applicativo dopo un cambio reale e richiesto della replica primaria |
| **2. HA zone-redundant** | Repliche distribuite tra availability zone della stessa regione; aggiunge protezione dalla perdita di una zona | **Non abilitata** sul database | Non coperto: il test non rende indisponibile una zona SQL |
| **3. Geo-replication / disaster recovery** | Database secondario in un'altra regione, tramite active geo-replication o failover group | **Non configurata** | Non coperto: nessun cambio di regione, endpoint geografico o test di disaster recovery |

Business Critical usa un'architettura simile agli Always On Availability Groups:
una primaria serve letture e scritture e replica sincronicamente le modifiche
alle repliche HA. Il protocollo assicura la persistenza sulle repliche necessarie
prima di confermare il commit. Azure gestisce numero, collocazione e promozione
delle repliche; queste fanno parte del servizio Business Critical, non sono
database separati da aggiungere o fatturare come copie indipendenti.

`zoneRedundant: false` **non disabilita l'HA**, ma non garantisce che le repliche
sopravvivano alla perdita di un'intera zona. Il backup SQL configurato con
ridondanza `Zone` è una proprietà distinta: non rende zone-redundant il compute.

In caso di failover, Azure promuove una replica HA e mantiene lo stesso hostname
SQL e la stessa connection string. Le connessioni aperte e le operazioni in corso
possono interrompersi: la trasparenza dell'endpoint non elimina la necessità di
riconnessione e gestione degli errori transitori nell'applicazione.

### Fault previsto: cambio della primaria HA locale

Il percorso proposto è:

```text
Scenario custom sql-local-ha-failover
  -> Action Chaos Studio StartRunbook
  -> Runbook Azure Automation con managed identity dedicata
  -> API Azure SQL databases/{database}/failover, replica primaria
  -> Attesa del completamento e registrazione dell'esito
```

Non useremo l'Action nativa `urn:csci:microsoft:sql:failover/1.0.0`:
opera su replication link o failover group e richiede geo-replication.
Il runbook userà invece il control plane ARM, senza connessione T-SQL,
password SQL o accesso ai secret Redis.

I nomi seguono le convenzioni del progetto, senza hash:
Automation Account `chaos-todo-dev-automation`, user-assigned identity
`chaos-todo-dev-sql-failover-mi`, runbook e scenario `sql-local-ha-failover`.
Il managed identity del Workspace può leggere i runbook e avviare/leggere i job
solo nell'Automation Account dedicato. L'identità dedicata dell'Automation Account
può leggere e richiedere il failover sul solo database target; per il polling
dell'header ARM `Location`, può inoltre leggere i risultati delle operazioni SQL
nel resource group. La shared identity applicativa e l'identità SQL admin restano
invariate e non ricevono privilegi di failover. Chi esegue il deploy deve poter
caricare il draft, pubblicare runbook e leggere il contenuto dell'Automation Account.

Il deploy crea o aggiorna l'infrastruttura e lo scenario, **non avvia un failover**.
L'esecuzione del fault è un'operazione esplicita: avvia lo Scenario da Chaos Studio.
Queste risorse sono publish-only, senza operazioni Azure durante `aspire run`.

### Cosa misureremo

| Aspetto | Evidenza da raccogliere |
|---|---|
| Riconnessione e connection pool | L'API sostituisce le connessioni interrotte e recupera senza restart manuale |
| Retry EF Core / SqlClient | Configurazione effettiva dei retry, errori transitori recuperati e richieste che terminano con errore |
| Disponibilità e latenza | Richieste riuscite/fallite, timeout e percentili di latenza prima, durante e dopo il fault |
| Tempo di recupero applicativo | Intervallo dai primi sintomi al ritorno stabile delle risposte corrette, distinto dal completamento dell'operazione ARM |
| Protezione FusionCache | Letture servite da cache, accessi alla factory SQL e comportamento fail-safe quando il database non risponde |
| Scritture e consistenza | Presenza dei Todo confermati prima del fault ed esito delle scritture durante la transizione |

La prima prova userà carico su `/api/todos/nocache`, per esporre direttamente
l'interruzione SQL. Una seconda prova userà `/api/todos` con cache preriscaldata,
per osservare la protezione di FusionCache. Le prove sono separate e non
combineranno inizialmente anche il flush Redis, che introdurrebbe un secondo fault.
Le richieste di scrittura saranno controllate per poter riconciliare gli esiti.
Azure limita le richieste di failover a una ogni 15 minuti per database o pool:
non eseguire le prove in rapida successione. Il runbook attende al massimo 10 minuti
l'esito dell'operazione ARM; al timeout l'operazione potrebbe essere ancora in corso,
quindi verificare lo stato prima di ritentare.

La latenza artificiale di 800 ms della demo è parte del baseline e va distinta
dal rallentamento causato dal failover. Un failover rapido potrebbe produrre
soltanto un picco di latenza senza errori HTTP: non bisogna promettere un numero
minimo di errori o una durata di indisponibilità.

**Integrità dei dati non significa successo di tutte le richieste.** Un failover
HA coordinato preserva i dati committed, ma una transazione in corso può fallire.
Inoltre il commit può riuscire mentre la risposta al client viene persa:
il chiamante non sa se la scrittura sia stata applicata. Ripetere un `POST`
senza idempotenza applicativa può creare duplicati; il failover non fornisce
semantica exactly-once alle richieste HTTP.

### Cosa non potremo affermare

Il fault sarà un **failover reale ma richiesto e coordinato**, non la distruzione
improvvisa di un nodo o l'isolamento di una zona. Non scegliamo la replica di
destinazione e il test non riproduce tutti gli effetti di un guasto non pianificato.
Non valida perdite di zona o regione, interruzioni prolungate della rete o di
Microsoft Entra, geo-failover, ripristino dei backup o scenari con perdita dati.

Anche abilitando in futuro `zoneRedundant: true`, lo stesso runbook testerà
"il recupero dell'app durante un failover di un database zone-redundant":
**non dimostrerà di avere simulato la perdita di una zona SQL**, né garantirà
che il cambio di primaria Business Critical avvenga verso una zona diversa.

### Disponibilità della ridondanza di zona in Italy North

Il primo deployment zone-redundant è stato rifiutato con `ProvisioningDisabled`.
Nell'indagine del 4 ottobre 2026, le capabilities SQL della sottoscrizione MVP
per Italy North, interrogate con API `2023-08-01` e `2025-01-01`, riportavano
Business Critical disponibile, ma `zoneRedundant: false` a livello di edizione
e `zoneRedundant: true` per la SKU `BC_Gen5_2` e la manutenzione `SQL_Default`.
Questi segnali discordanti non dimostrano un'assenza generale del supporto regionale,
né identificano una causa certa legata al benefit MVP, alle quote o alla capacità.

Il database attuale usa `SQL_Default` ed è online senza ridondanza di zona.
Prima di cambiare flag, SKU o regione occorre chiarire con Azure Support
la disponibilità per questa sottoscrizione. Il test HA locale non richiede
di risolvere prima questa restrizione.

Riferimenti: [architettura HA e ridondanza di zona SQL](https://learn.microsoft.com/azure/azure-sql/database/high-availability-sla-local-zone-redundancy),
[API Database Failover](https://learn.microsoft.com/rest/api/sql/databases/failover)
e [catalogo scenari Chaos Studio](https://learn.microsoft.com/azure/chaos-studio/chaos-studio-scenarios).

## Personalizzare

- `Demo:SimulatedDbLatencyMs` in `ChaosToDo.Api/appsettings.json` — alza il valore se vuoi
  rendere ancora più evidente la differenza cache/no-cache durante la demo.
- La SKU di Azure SQL (`BC_Gen5`, capacity 2) e del piano App Service (`P1v3`, capacity 2) sono
  configurate in `ChaosToDo.AppHost/AppHost.cs` — riducile se vuoi contenere i costi al di fuori
  della demo.
