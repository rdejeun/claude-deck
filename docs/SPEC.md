# ClaudeDeck — spécification v1

## 1. Objectif et contraintes

Utiliser un Stream Deck Mini (6 touches LCD 80 × 80, 3 × 2) comme moniteur d'activité de Claude Code sous Windows 10.

- Fonctionner **sans pilote ni logiciel Elgato** : accès HID direct.
- Tourner en arrière-plan (icône de notification), démarrer avec Windows, sans droits administrateur.
- Ne jamais gêner Claude Code : hooks non bloquants, aucune décision de permission prise par ClaudeDeck.
- Sessions suivies : CLI (Windows Terminal, terminal VS Code) et extension VS Code. Sessions cloud hors périmètre v1.

## 2. Architecture

```
Claude Code ──hooks « http »──────────────► POST /hook ─┐
     │                                                  │      ┌──────────────┐
     └─status line─► claude-deck.exe statusline         ├─────►│ SessionTracker│──┐
                         └──► POST /statusline ─────────┤      └──────────────┘  │
                                                        └─────►│ PlanUsageStore│──┤
Admin API (cost_report) ◄── UsageService ──────────────────────►              ──┤
/api/oauth/usage (option)◄─┘                                                    ▼
                                                    DeckController ─ KeyViewBuilder ─ KeyRenderer (Skia)
                                                         │                              │
                         Stream Deck Mini ◄── DeviceWriter ◄── images ──────────────────┘
                         (HidSharp)      ──► appuis ──► focus fenêtre (Win32) / rafraîchissement
                         Page web locale ◄── /settings, /preview, /api/*
```

| Projet | Rôle |
|---|---|
| `src/ClaudeDeck.Core` (net8.0) | Tout le métier, multiplateforme : état, jauges, rendu, HID, serveur HTTP, installateur de `settings.json`. |
| `src/ClaudeDeck` (net8.0-windows) | Exécutable Windows : icône de notification, focus des fenêtres, DPAPI, démarrage auto (sur demande), commandes `statusline` / `install` / `uninstall`. |
| `src/ClaudeDeck.Sim` | Simulateur multiplateforme (daemon sans matériel, scénario de démo, galerie PNG). |
| `tests/ClaudeDeck.Tests` | Tests xUnit (machine à états, répartition, jauges, BMP, installateur, serveur HTTP). |

## 3. Intégration Claude Code

### Hooks

Ajoutés à `~/.claude/settings.json` (sans matcher = tous) pour : `SessionStart`, `SessionEnd`, `UserPromptSubmit`,
`PreToolUse`, `PostToolUse`, `PostToolUseFailure`, `PermissionRequest`, `PermissionDenied`, `Notification`, `Stop`,
`StopFailure`, `SubagentStart`, `SubagentStop`, `PreCompact`, `PostCompact`, `PostModelSwitch`.

```json
{ "hooks": [ { "type": "http", "url": "http://127.0.0.1:47777/hook", "timeout": 3,
               "headers": { "X-ClaudeDeck-Token": "<jeton aléatoire>" } } ] }
```

Le daemon répond `200` avec un corps vide (= « exit 0 sans sortie ») : aucune influence sur Claude Code.
Une connexion refusée (daemon arrêté) est une erreur non bloquante.

**Identification du processus** : à la première requête d'une connexion TCP, le daemon cherche dans la table TCP
(`GetExtendedTcpTable`) le PID propriétaire du port client : c'est le processus Claude Code. Il sert à
(a) retrouver la fenêtre hôte en remontant les parents (claude → shell → `WindowsTerminal.exe` / `Code.exe`),
(b) oublier la session quand le processus meurt sans `SessionEnd`.

### Status line

`claude-deck.exe statusline` lit le JSON sur stdin, le transmet au daemon (`POST /statusline`, 800 ms max), puis
affiche la status line préexistante de l'utilisateur (exécutée via Git Bash, sinon PowerShell) ou un texte compact
`Opus 5.5 · ctx 34% · 5h 23% →17:00 · 7j 41%`. Champs utilisés : `rate_limits.five_hour|seven_day.used_percentage|resets_at`,
`model.display_name`, `context_window.used_percentage`, `workspace.*`, `session_name`, `cost.total_cost_usd`.

## 4. Machine à états des sessions

| Événement | Effet |
|---|---|
| `SessionStart` | → Au repos (création) |
| `UserPromptSubmit` | → Travaille, début du tour |
| `PreToolUse` | → Travaille, outil courant ; `AskUserQuestion` / `ExitPlanMode` → En attente (question) |
| `PermissionRequest`, `Notification[permission_prompt]` | → En attente (permission) |
| `Notification[elicitation_dialog]` | → En attente (question) |
| `PostToolUse`, `PostToolUseFailure`, `PermissionDenied` | → Travaille, plus d'outil courant |
| `Notification[idle_prompt]` | si Travaille/Compaction → Au repos (tour interrompu par Échap, sans `Stop`) |
| `Stop` | → Terminé (durée du tour mémorisée) |
| `StopFailure` | → Erreur |
| `PreCompact` / `PostCompact` | → Compaction / retour à Travaille ou Au repos |
| `SubagentStart` / `SubagentStop` | compteur d'agents (≥ 0) |
| `SessionStart.model`, `PostModelSwitch.to_model` | modèle affiché (« claude-opus-5-5 » → « Opus 5.5 ») ; aussi mis à jour par la status line (`model.display_name`) et par la lecture du transcript (`transcript_path`, dernière réponse de l'agent principal, après chaque événement et toutes les 15 s au travail) — seule source pour l'extension VS Code. Le transcript donne aussi le remplissage du contexte (jetons d'entrée + cache) ; la taille de fenêtre vient de la status line pour ce modèle si elle a été vue, sinon elle est supposée (200 000, ou 1 M au-delà) et le % est affiché « ≈ » |
| `SessionEnd` | suppression |
| appui sur la touche | Terminé/Erreur → Au repos |
| maintenance (10 s) | Terminé/Erreur → Au repos après 30 min ; suppression si processus mort ou 12 h sans événement |

**Répartition sur 3 touches** : tri par priorité (attente 0, erreur 1, travaille/compaction 2, terminé 3, repos 4)
puis activité récente ; affectations collantes ; « +N » (orange si une session masquée attend) sur la 3ᵉ touche.

## 5. Jauges

| Touche | Source | Rafraîchissement |
|---|---|---|
| Session 5 h | status line `rate_limits.five_hour` ; option : `GET https://api.anthropic.com/api/oauth/usage` (non documenté) | à chaque status line ; OAuth toutes les 5 min si aucune donnée fraîche |
| Semaine | idem, `seven_day` | idem |
| Bas à droite : logo (défaut) | Motif vectoriel, ou image de l'utilisateur (`display.logoImagePath`). Appui : ouvre `display.logoUrl` (défaut `https://claude.ai/new`, http/https seulement) via le navigateur par défaut ; avec `display.logoUrlNewWindow`, lance l'exécutable du navigateur (lu dans `UrlAssociations\https\UserChoice`) avec `-new-window` / `--new-window` | — |
| Bas à droite : API 7 j (option `display.bottomRightKey = "api"`) | `GET /v1/organizations/cost_report?bucket_width=1d` (clé Admin ou clé personnelle sans espace de travail ; compte d'organisation requis ; montants en cents) | 15 min, ou appui |

Seuils de couleur : bleu < 90 %, orange < 95 %, rouge ≥ 95 %. Donnée de plus de 30 min : grisée et préfixée `~`.
Fenêtre dont `resets_at` est passé : « réinitialisé ».

**Solde estimé** = solde saisi − (dépense depuis le début du jour UTC de saisie − dépense de ce jour déjà constatée
au moment de la saisie). Le rapport de coûts n'ayant qu'une granularité journalière, la « ligne de base » évite de
compter deux fois la dépense de la journée de saisie. Alerte (fond rouge) sous un seuil configurable (5 $).

## 6. Protocole HID du Stream Deck Mini

VID `0x0FD9`, PID `0x0063` (Mini) ou `0x0090` (Mini MK.2) — liste configurable.

| Opération | Rapport |
|---|---|
| Image d'une touche | Sortie, 1024 octets : `02 01 <page> 00 <dernière> <touche+1> 00…` (16 octets) + 1008 octets de BMP |
| Luminosité | Fonction : `05 55 AA D1 01 <0-100>` |
| Réinitialisation | Fonction : `0B 63` |
| Touches | Entrée : `01 <k1> … <k6>` (0/1) |

Animations : horloge en millisecondes (clignotement 1 Hz, astérisque 14 étapes égales sur 2 s, déphasé d'un tiers de cycle par touche, ou GIF
personnel décodé par SkiaSharp), rendu à `display.animationFps` (12 par défaut) ; une image n'est renvoyée que si
la touche a changé.

Image : BMP 24 bits 80 × 80 (lignes de bas en haut, BGR), après rotation 90° anti-horaire puis miroir vertical
(= transposition). Rotation et miroirs sont configurables et vérifiables avec la mire de test.

Écriture sur un thread dédié qui ne garde que la dernière image par touche (les images intermédiaires d'une animation
sont sautées si l'USB est plus lent). Reconnexion automatique (notification USB + nouvelle tentative toutes les 5 s).

## 7. API HTTP locale (127.0.0.1:47777)

| Route | Jeton | Rôle |
|---|---|---|
| `POST /hook` | oui | hooks Claude Code |
| `POST /statusline` | oui | JSON de status line → texte |
| `GET /settings`, `GET /preview` | — | pages (le jeton y est injecté) |
| `GET /key/{0-5}.png` | — | image courante d'une touche |
| `GET /api/state` | oui | état complet (JSON) |
| `POST /api/press/{k}`, `/api/test-pattern`, `/api/refresh` | oui | actions |
| `POST /api/settings`, `/api/admin-key`, `/api/balance` | oui | configuration |
| `POST /api/install`, `/api/uninstall` | oui | intégration Claude Code |

Contrôles : en-tête `Host` ∈ {`127.0.0.1:port`, `localhost:port`}, `Origin` absent ou local pour `/api/*`,
comparaison du jeton à temps constant. Serveur HTTP/1.1 maison (keep-alive, chunked, `Expect: 100-continue`) :
`HttpListener` exigerait des droits administrateur pour `127.0.0.1` sous Windows.

## 8. Distribution et antivirus

Binaire non signé : pour limiter les faux positifs heuristiques, ClaudeDeck évite les comportements typiques des
logiciels malveillants.

| Évité | Remplacé par |
|---|---|
| Exécutable unique compressé, extraction de DLL natives dans `%TEMP%` | Publication en dossier classique (variantes « light » framework-dependent et « portable ») |
| Auto-copie dans `%LOCALAPPDATA%` | Exécution sur place ; l'intégration pointe vers l'emplacement courant, avec alerte si l'exécutable a été déplacé |
| Inscription automatique dans la clé `Run` | Option explicite du menu |
| Saisie clavier synthétique (`keybd_event`) pour forcer le premier plan | `SetForegroundWindow` + `AttachThreadInput` ; sinon clignotement dans la barre des tâches |

Restent nécessaires et assumés : lecture de la table TCP et de l'arborescence des processus (identifier la fenêtre
de la session), DPAPI (clé Admin), lecture optionnelle de `~/.claude/.credentials.json` (source OAuth, désactivée par
défaut). Piste : signature via un programme gratuit pour projets open source (ex. SignPath Foundation) ou Azure Trusted Signing.

## 9. Validation sur le matériel (à faire par l'utilisateur)

1. Mire de test : orientation correcte sur les 6 touches.
2. Appuis : chaque touche physique déclenche l'action attendue (focus pour 1-3, rafraîchissement pour 4-6).
3. Débrancher / rebrancher, mise en veille / réveil : l'affichage revient seul.
4. Session CLI dans Windows Terminal et dans VS Code, puis extension VS Code : états, durées, focus.
5. Permission demandée : la touche clignote orange ; elle repasse au bleu après réponse.

## 10. Pistes v2

Sessions claude.ai/code (extension Firefox ou hooks cloud + relais), extension VS Code pour cibler le bon terminal
ou onglet, approbation des permissions depuis le deck (hook `PermissionRequest` bloquant), autres modèles de Stream Deck.
