# FiveM Clip Patcher

App Windows pour virer les références de mods dans les fichiers `.clip` FiveM / GTA V qui font crasher **Rockstar Editor**.

Port GUI du script Python [WaydeTheKiwi/FiveM_Clip_Patcher](https://github.com/WaydeTheKiwi/FiveM_Clip_Patcher) (CC BY-NC-SA 4.0).

## Exe

`publish\FiveMClipPatcher.exe`

Self-contained, un seul fichier, pas besoin d'installer Python ni .NET.

## Usage

1. Ferme GTA V / FiveM.
2. Lance l'exe — les séquences s'affichent avec miniature, nom et date.
3. **Coche** une ou plusieurs séquences (boutons Tout cocher / Tout décocher).
4. **Scanner la sélection** pour voir les matches sans modifier les fichiers.
5. **Patcher la sélection** — confirmation, backup auto, remplacement in-place (même taille).

Dossier par défaut : `%LOCALAPPDATA%\Rockstar Games\GTA V\videos\clips`

Exact (`17mov_foo`) cherche une substring dans tout le binaire. Wildcard (`17mov_*`) ne matche qu'un nom de ressource isolé (run ASCII entière).

## Patterns par défaut

Trois catégories sont préchargées dans l'onglet **Patterns** :

1. **Mods courants** — liste du [README WaydeTheKiwi](https://github.com/WaydeTheKiwi/FiveM_Clip_Patcher) (`17mov_*`, `scully_*`, `*_emotemenu`, `bzzz_*`, `pprp_*`, …)
2. **Crashs documentés** — ressources citées dans les rapports publics build 3258 / GitHub (`griz_cayo_restaurant`, `amb-roxwood-interiors`, `prompt_vfd_4bays`)
3. **Créateurs MLO** — wildcards fréquents sur serveurs avec assets escrow (`prompt_*`, `gabz_*`, `kiiya_*`, `k4mb1_*`, `molo_*`)

Le crash dépend surtout des ressources **de ton serveur**. Utilise **Détecter** pour analyser tes `.clip` et ajouter automatiquement les noms de ressources les plus fréquents (section `# Suggérés depuis tes clips`).

Pour un crash persistant, ouvre le log FiveM et cherche `during loading of <nom_ressource>` — ajoute ce nom en pattern **exact**.

## Build

```bat
dotnet publish src\FiveMClipPatcher\FiveMClipPatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\
```
