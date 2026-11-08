# ClaudeDeck

Moniteur d'activité **Claude Code** sur **Stream Deck Mini** (6 touches), pour Windows 10/11.
**Aucun pilote ni logiciel Elgato requis** : ClaudeDeck parle directement au périphérique via le pilote HID générique de Windows.

```
┌───────────────┬───────────────┬───────────────┐
│  session 1    │  session 2    │  session 3 +N │   ← sessions Claude Code (couleur = état, appui = focus)
├───────────────┼───────────────┼───────────────┤
│ SESSION · 5 H │   SEMAINE     │   API · 7 J   │   ← limites du forfait Pro/Max, dépense API
│ 62 % → 18:43  │ 23 % → jeu.   │ 41,27 $ ≈118 $│
└───────────────┴───────────────┴───────────────┘
```

![États des touches](docs/images/etats-des-touches.png)

## Ce que ça affiche

**Ligne du haut — jusqu'à 3 sessions Claude Code locales** (CLI dans Windows Terminal ou VS Code, extension VS Code).
Priorité automatique : *en attente* > *erreur* > *travaille* > *terminé* > *au repos*, puis la plus récente.
Une session garde sa touche tant qu'elle reste affichée ; la pastille « +N » signale les sessions masquées
(orange si l'une d'elles attend une action).

| État | Couleur | Déclencheur (hooks Claude Code) |
|---|---|---|
| Au repos | gris | `SessionStart`, Échap (`Notification` idle) |
| Travaille | bleu, anneau animé, outil en cours, durée du tour | `UserPromptSubmit`, `PreToolUse`, `PostToolUse` |
| **Autoriser ? / Question** | **orange clignotant** | `PermissionRequest`, `Notification` (permission, elicitation), `AskUserQuestion` |
| Terminé | vert, « il y a 3 min » | `Stop` |
| Compaction | violet | `PreCompact` → `PostCompact` |
| Erreur API | rouge | `StopFailure` |

**Appui sur une session** : met au premier plan la fenêtre qui l'héberge (VS Code — la bonne fenêtre si plusieurs —
ou Windows Terminal) et acquitte l'état « terminé ».

**Ligne du bas — jauges**

1. **Session (5 h)** du forfait Pro/Max : % consommé et heure de réinitialisation.
2. **Semaine** : % consommé et jour + heure de réinitialisation.
3. **API · 7 jours** : dépense de l'organisation (Admin API) et **solde estimé**.

Appuyer sur une jauge force un rafraîchissement. L'écran baisse en veille après 15 min sans activité
(jamais si une session travaille ou attend), et s'éteint quand Windows est verrouillé.

## Installation

1. Téléchargez `claude-deck-win-x64.zip` depuis les [Releases](https://github.com/rdejeun/claude-deck/releases)
   (ou l'artefact de la dernière exécution de la CI), décompressez, lancez `claude-deck.exe`.
2. **Quittez le logiciel Stream Deck d'Elgato** s'il est installé (et désactivez son démarrage automatique) :
   il se disputerait l'écran. ClaudeDeck vous prévient s'il le détecte.
3. Clic droit sur l'icône ClaudeDeck (zone de notification) → **Installer l'intégration Claude Code**. Cela :
   - copie l'exécutable dans `%LOCALAPPDATA%\ClaudeDeck\` et l'inscrit au démarrage de Windows (clé `Run` de HKCU, sans droits admin) ;
   - ajoute dans `%USERPROFILE%\.claude\settings.json` des hooks de type `http` vers `http://127.0.0.1:47777/hook`
     et une status line `claude-deck.exe statusline` (sauvegarde horodatée du fichier avant écriture ;
     vos hooks existants sont conservés, et votre status line existante continue de s'afficher).
4. Redémarrez vos sessions Claude Code (les hooks sont lus au démarrage).
5. Menu → **Mire de test** : chaque touche doit afficher son numéro (1-2-3 en haut), une flèche vers le haut et un carré
   rouge en haut à gauche. Sinon, ajustez rotation / miroirs dans les réglages.

En ligne de commande : `claude-deck.exe install` / `claude-deck.exe uninstall`.

### Réglages (page web locale)

Double-clic sur l'icône → `http://127.0.0.1:47777/settings` : aperçu en direct des 6 touches (cliquables),
sessions suivies, jauges, luminosité, veille, orientation, et :

- **Clé Admin API** (`sk-ant-admin…`, Console → Settings → Admin keys) : stockée chiffrée par DPAPI dans votre profil
  Windows, utilisée uniquement pour `GET /v1/organizations/cost_report`.
- **Solde** : aucune API publique ne fournit le solde de crédit prépayé. Saisissez le montant affiché dans la Console
  (Billing) après chaque recharge ; ClaudeDeck affiche ensuite `solde ≈ saisi − dépense mesurée depuis`.

Fichiers : `%APPDATA%\ClaudeDeck\` (`config.json`, `plan-usage.json`, `logs\claude-deck.log`, secret chiffré).

## Sources des jauges du forfait — à savoir

Les pourcentages 5 h / semaine viennent du champ officiel `rate_limits` que Claude Code transmet à la **status line**
(Pro/Max uniquement, après la première réponse de la session). Conséquences :

- ils sont mis à jour **quand une session CLI tourne** ; sinon la dernière valeur reste affichée, puis grisée (`~`) après 30 min ;
- **l'extension VS Code n'exécute pas de status line** : si vous n'utilisez qu'elle, activez dans les réglages
  « Source OAuth non documentée ». ClaudeDeck interroge alors toutes les 5 min l'endpoint `/api/oauth/usage` qu'utilise
  la commande `/usage`, avec le jeton de `~/.claude/.credentials.json` (lecture seule, jamais rafraîchi par ClaudeDeck).
  Cet endpoint n'est **pas documenté** : il peut changer ou disparaître sans préavis. Désactivé par défaut.

## Sécurité

- Le serveur n'écoute que sur `127.0.0.1`, vérifie l'en-tête `Host` (anti DNS-rebinding) et exige un jeton aléatoire
  (`X-ClaudeDeck-Token`, inscrit dans les hooks) : une page web ne peut pas forger d'événements.
- Les hooks répondent immédiatement avec un corps vide : ClaudeDeck **n'approuve ni ne refuse jamais rien** à votre place.
  Si le daemon est arrêté, Claude Code continue normalement (échec de hook non bloquant).

## Limites connues (v1)

- Sessions **claude.ai/code (cloud)** non suivies.
- Windows Terminal : le focus amène la bonne fenêtre, pas le bon onglet.
- Console Windows « classique » (conhost) : le focus peut ne pas trouver la fenêtre.
- Windows peut refuser le changement de premier plan dans certains cas (l'icône de la barre des tâches clignote alors).
- Exécutable de 83 Mo (runtime .NET embarqué, compressé) : aucun prérequis à installer.

## Développement

Prérequis : SDK .NET 8. Tout se compile aussi sous Linux/macOS (sauf l'exécution de l'appli Windows).

```bash
dotnet test tests/ClaudeDeck.Tests
```

```bash
dotnet run --project src/ClaudeDeck.Sim -- --demo
```

Le simulateur lance le daemon complet sans matériel, avec un scénario de démonstration ; aperçu sur
`http://127.0.0.1:47777/preview`. `--gallery <dossier>` rend chaque état de touche en PNG.

```bash
dotnet publish src/ClaudeDeck -c Release -o publish
```

Architecture et protocole : [docs/SPEC.md](docs/SPEC.md).

## Licence

MIT. Composants tiers : [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
« Stream Deck » est une marque d'Elgato ; projet non affilié à Elgato ni à Anthropic.
