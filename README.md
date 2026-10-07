# ChaosToDo

Demo di Chaos Engineering su Azure: una API Todo in **.NET 10 e Aspire 13.6.0**
per osservare perdita del compute di una zona, cache stampede e failover SQL.
Questa guida spiega come ricostruire l'ambiente e ripetere le demo.
I dettagli dei template e dei publisher sono in [infra.md](infra.md).

## Architettura

Lo stesso codice API usa Azure SQL Database e Azure Managed Redis condivisi,
ma viene distribuito su due hosting:

| Hosting/servizio | Configurazione | Demo |
|---|---|---|
| VM scale set Linux + Standard Load Balancer | 2 VM, una in zona 2 e una in zona 3 | Compute Zone Down |
| App Service Windows | P1v3, 2 worker, non zone-redundant | Cache e SQL |
| Azure SQL Database | Business Critical, 2 vCore, HA locale, non zone-redundant | Cambio della replica primaria |
| Azure Managed Redis | Balanced_B0 | Flush della cache |
| Chaos Studio + Automation | 4 scenari custom e runbook SQL | Iniezione e monitoraggio dei fault |

App Service Kill Process riavvia subito il processo; non mantiene spenta una zona.
Lo shutdown VMSS mantiene invece spente le VM selezionate per la durata del fault.
Aspire orchestra l'app localmente e il deploy, ma non viene distribuito come processo.

## Prerequisiti

- .NET 10 SDK, Aspire CLI 13.6.0 e PowerShell 7.
- Docker Desktop (o un motore compatibile) per l'esecuzione locale.
- Azure CLI autenticata e subscription Azure per le demo cloud.
- Permessi di provisioning, creazione ruoli custom e assegnazione RBAC nello scope
  scelto (per esempio Contributor + Role Based Access Control Administrator).
- Quota e capacita' per 2 VM, App Service P1v3, SQL Business Critical e Managed Redis.

**L'ambiente ha costi anche senza fault in esecuzione.** E' una configurazione da
demo, non un template production-ready: include endpoint pubblici e permessi SQL
ampi per consentire le migrazioni. Vedi [limiti infrastrutturali](infra.md#limiti-e-personalizzazione).

## Eseguire in locale

Dalla root del repository, con Docker avviato:

```powershell
Set-Location .\ChaosToDo.AppHost
aspire run
```

Aspire avvia API, SQL Server e Redis locali e apre la dashboard. Non provisiona
VM, App Service, Automation o Chaos Studio. Il container SQL e' persistente.
La UI OpenAPI dell'API e' disponibile su `/scalar/v1`.

| Endpoint | Comportamento |
|---|---|
| `GET /api/todos` | FusionCache: L1 in memoria + L2 Redis, protezione stampede per processo e fail-safe |
| `GET /api/todos/naive` | Cache-aside solo Redis, senza lock/L1/fail-safe; header `X-Cache: HIT\|MISS` |
| `GET /api/todos/nocache` | Ogni richiesta interroga SQL |
| `GET /api/todos/{id}` | Singolo Todo con FusionCache |
| `POST`, `PUT`, `DELETE /api/todos...` | CRUD e invalidazione della cache |

Le letture dal database includono **800 ms di latenza simulata**, configurabile
con `Demo:SimulatedDbLatencyMs` in `ChaosToDo.Api\appsettings.json`.

## Ricostruire l'ambiente Azure

### 1. Configurazione

Dalla root:

```powershell
az login
az account set --subscription "<subscription-id>"
Copy-Item .\ChaosToDo.AppHost\appsettings.dev.json.sample .\ChaosToDo.AppHost\appsettings.dev.json
```

Modificare `appsettings.dev.json` (escluso da Git) con subscription, regione e
Resource Group propri. Il template di esempio usa `italynorth`; il workspace
Chaos usa `northeurope` nel template custom.

I nomi derivano da `chaos-todo` e dall'ambiente (`dev`). I nomi globali di sito,
SQL, vault, storage e DNS devono essere disponibili: per un clone indipendente
personalizzare `projectName` in `ChaosToDo.AppHost\AppHost.cs` prima del deploy.
Usare Resource Group distinti per ambienti diversi, poiche' il workspace ha
nome fisso `chaostodo-workspace`.

Le opzioni `Demo:VmApiSize` e `Demo:WindowsAppServiceWorkerCount` permettono
di scegliere la size VM (default `Standard_D2als_v7`) e i worker Windows
(default 2, ammessi 1-3). Per questa demo mantenere 2 worker e una VM per zona.
Controllare la disponibilita' della size nelle **zone 2 e 3** della regione scelta:
una quota sufficiente non garantisce capacita' allocabile.

### 2. Publish e deploy

```powershell
Set-Location .\ChaosToDo.AppHost
aspire publish -e dev --non-interactive
aspire deploy -e dev --non-interactive
```

`publish` genera template e pacchetti, senza distribuire risorse. `deploy`
provisiona le risorse, distribuisce l'API su entrambi gli hosting, pubblica il
runbook e aggiorna discovery, valutazione e configurazioni Chaos.
**Nessuno dei due comandi avvia fault.** Usare sempre lo stesso `-e dev`:
senza `-e`, l'ambiente predefinito e' `Production`.

Gli artefatti `aspire-output` sono generati: modificare AppHost e template sorgenti,
non l'output. Per aggiornare solo il codice su hosting gia' configurati:

```powershell
aspire do deploy-windows-api-code -e dev
aspire do deploy-vm-api-code -e dev
```

### 3. Preparare portale e terminali

Tornare alla root del repository per eseguire gli script. Recuperare dal riepilogo
del deploy **Windows API readiness** e **VM API endpoint** e usare i propri URL:

```powershell
$appServiceUrl = "https://<nome-sito>.azurewebsites.net"
$vmUrl = "http://<dns-load-balancer>"
```

Gli script hanno default dell'ambiente originale: passarvi sempre `-BaseUrl`
quando si ricostruisce il progetto. Per il monitor VMSS impostare anche
`-ResourceGroup` e `-VmssName`, e selezionare la subscription corretta in Azure CLI.

Nel portale aprire **Chaos Studio -> Workspaces -> chaostodo-workspace** nel proprio
Resource Group. Per ogni demo selezionare lo scenario indicato, configurazione
**`default`**, controllare target/parametri e validare prima di eseguire **Run**.
Chi avvia il run deve avere i permessi di esecuzione sul workspace.
Attendere la fine del run e il recupero prima di passare al successivo.

## Demo 1: Compute Zone Down

**Obiettivo:** mostrare che il Load Balancer rimuove il compute indisponibile di una
zona e continua a servire dalla zona superstite, con capacita' e ridondanza ridotte.

```powershell
.\scripts\zone-down-dashboard.ps1 -BaseUrl $vmUrl -ResourceGroup "<rg>" -VmssName "<vmss>"
```

1. Attendere un baseline con risposte da `zone-2` e `zone-3` e VMSS `running`.
2. Nel portale eseguire **`compute-zone-down` / `default`**: zona 2, **2 minuti**.
3. Osservare `zone-2` senza risposte e la riga VMSS `stopped`. Possono comparire
   timeout o picchi di latenza mentre il probe converge; poi risponde solo `zone-3`.
4. Al termine attendere sia VMSS `running` sia il ritorno delle risposte da zona 2.
   Accensione della VM e rientro nel pool non sono simultanei.

La dashboard mostra completamenti, errori, timeout, latenza e risposte per zona.
Usa una nuova connessione TCP per richiesta per non fissare il traffico su una VM.
Il polling VMSS usa Azure CLI; `-PowerStateIntervalSeconds 0` lo disabilita.
Non simula un outage Azure completo: SQL e Redis non vengono fermati.

## Demo 2: Cache Stampede

**Obiettivo:** confrontare cache-aside ingenuo e FusionCache sotto lo stesso flush.
Aprire due terminali affiancati e definire `$appServiceUrl` in entrambi:

```powershell
# Terminale 1
.\scripts\cache-dashboard.ps1 -BaseUrl $appServiceUrl -Endpoint naive

# Terminale 2
.\scripts\cache-dashboard.ps1 -BaseUrl $appServiceUrl -Endpoint fusion
```

1. Attendere il riscaldamento: entrambi i worker visibili, naive HIT, query prossime a 0.
2. Eseguire **`cache-stampede` / `default`** nel portale.
3. Nel naive osservare MISS concorrenti, query SQL multiple e latenza piu' alta.
   Dopo che Redis viene ripopolato tornano gli HIT.
4. In FusionCache la L1 puo' continuare a rispondere senza query dovute al flush.
   Non e' solo un lock: la prima difesa visibile e' la copia in memoria.

La dashboard mostra richieste, errori, HIT/MISS, p50/p95/max e, per worker,
richieste/query. I cookie ARR sono disabilitati. I contatori derivano dagli header
`X-Served-By`, `X-Process-Id`, `X-Db-Queries`, ripartono al restart e la prima
osservazione di ogni processo stabilisce il baseline (non conta le query precedenti).

La durata FusionCache e' 5 minuti con eager refresh al 90%, fail-safe e soft timeout
di 500 ms. Un refresh normale puo' quindi produrre query anche senza fault.
Il flush esterno **non invalida la L1**; il numero di MISS dipende dalla concorrenza
effettiva, non e' garantito che coincida sempre con il numero di utenti.

## Demo 3: Cache Stampede con Process Crash

**Obiettivo:** aggiungere la perdita della L1 di un processo al flush di Redis.
Lasciare attive le due dashboard della demo cache e attendere il baseline.

1. Eseguire **`cache-stampede-with-process-crash` / `default`**.
2. Osservare prima lo stampede sul naive.
3. Dopo il successo del flush, Chaos avvia Kill Process. La dashboard evidenzia
   un nuovo PID con **`RESTART`** sul worker coinvolto; possono esserci errori
   e un picco di latenza durante il riavvio.
4. Confrontare query e recupero: se la entry FusionCache non e' piu' in Redis,
   le richieste concorrenti sul processo ripartito condividono una factory.
   Se Redis e' gia' stato ripopolato, il worker puo' recuperare dalla L2 senza query.

**Non promettere il cold start di entrambi i worker.** L'azione non seleziona i
worker per ID e non garantisce che entrambi perdano la L1; osservare i PID.
La protezione stampede e' **per processo**, non un lock distribuito globale.
`runAfter` ordina flush e kill, ma il traffico puo' ripopolare Redis tra le azioni.
Entrambe sono discrete: la durata dichiarata non mantiene cache vuota o processi
spenti per due minuti.

## Demo 4: Failover SQL HA locale

**Obiettivo:** osservare il recupero dell'API dopo un cambio reale, richiesto e
coordinato della primaria SQL, senza cache e senza cambiare connection string
o riavviare manualmente l'API.

```powershell
.\scripts\sql-failover-dashboard.ps1 -BaseUrl $appServiceUrl
```

1. Attendere un baseline su **`/api/todos/nocache`**: ogni richiesta interroga SQL,
   con circa 800 ms di ritardo simulato da distinguere dal fault.
2. Eseguire **`sql-local-ha-failover` / `default`** nel portale.
3. Osservare richieste riuscite, errori HTTP/rete, timeout, richieste in attesa
   e p50/p95/max. Un failover breve puo' causare solo maggiore latenza.
4. Nell'Automation Account controllare il job del runbook `sql-local-ha-failover`:
   attendere `Completed`, il run Chaos `Succeeded` e il recupero HTTP stabile.
   Affiancare l'Activity Log SQL per confermare il failover.

Business Critical include repliche HA locali anche con `zoneRedundant: false`.
Azure promuove una replica mantenendo lo stesso endpoint SQL; le connessioni in
corso possono interrompersi. Riconnessione e retry dell'API contano quanto l'HA:
non promettere zero errori. Lo script non aggiunge retry, non scrive dati e non
misura la consistenza delle scritture.

Non e' geo-failover, perdita di una zona SQL o guasto improvviso di un nodo.
**Lasciare almeno 15 minuti tra i failover dello stesso database.**
Se il runbook va in timeout/errore dopo il POST, SQL potrebbe proseguire:
controllare l'operazione prima di ripetere il fault.

## Leggere i risultati e terminare

Gli script stampano una riga al secondo, non avviano scenari e terminano con
`Ctrl+C`; `-DurationSeconds 300` limita la durata. Il carico e' a ciclo chiuso:
ogni utente aspetta la risposta prima di inviare la successiva. Zero completamenti
in un secondo non prova un outage totale. Verificare sempre l'esito del run
e il recupero effettivo, non soltanto la validazione.

Eseguire un fault alla volta: SQL e Redis sono condivisi tra i due hosting.
Fermare una dashboard **non cancella il fault**: usare il portale per gestire il run.
La demo termina solo dopo che le risorse e l'API sono tornate operative.

Per eliminare un ambiente dedicato, valutare `aspire destroy -e dev`
dall'AppHost: e' distruttivo e comprende anche database, Redis e Key Vault.
Rimuovere una risorsa dal modello non la elimina automaticamente.
