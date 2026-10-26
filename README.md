# mbsearch — musique électronique liégeoise

Application en ligne de commande (.NET 10 + Spectre.Console) qui recense, via **MusicBrainz** et **Discogs**,
les œuvres de musique électronique dont au moins un auteur est liégeois, avec ce qu'il faut pour les retrouver :
labels, numéros de catalogue, formats, pays, codes-barres, liens MusicBrainz / Discogs, recherches YouTube.

## Démarrage

```bash
export MUSICBRAINZ_CONTACT="votre@email"     # User-Agent identifiable exigé par MusicBrainz
export DISCOGS_TOKEN="xxxx"                  # facultatif : https://www.discogs.com/settings/developers

dotnet run --project src/MusicBrainzSearch -- areas                         # zones considérées comme liégeoises
dotnet run --project src/MusicBrainzSearch -- scan --max-artists 40         # essai rapide
dotnet run --project src/MusicBrainzSearch -- scan -o exports/liege.md -o exports/liege.csv -o exports/liege.json
dotnet run --project src/MusicBrainzSearch -- scan --scope province --belgium --show-artists
```

Le premier `scan` complet prend du temps (MusicBrainz impose 1 requête/s, Discogs 60/min avec token),
mais toutes les réponses sont mises en cache (30 jours par défaut, dans `%LOCALAPPDATA%/MusicBrainzSearch/cache`) :
les exécutions suivantes, ou une reprise après Ctrl+C, sont quasi immédiates.

## Stratégie de recherche

1. **Zones** : Liège (ville) ou la province, plus leurs sous-zones MusicBrainz (relations « part of » : anciennes communes, quartiers).
2. **Artistes de départ** : artistes dont la zone, la zone d'origine (naissance/fondation) ou de fin est liégeoise, plus ceux dont la description mentionne Liège.
3. **Entonnoir belge** (`--belgium`) : artistes belges tagués électro sans ville renseignée, retenus si leur profil Discogs cite Liège / Luik / Lüttich.
4. **Groupes et pseudonymes** : relations MusicBrainz (*member of band*, *is person*, *collaboration*), puis **Discogs** (`aliases`, `groups`).
   Chaque artiste Discogs est rapproché de MusicBrainz par son URL ; à défaut, il est conservé comme artiste « Discogs seul ».
5. **Filtre électronique** : tags et genres de l'artiste et de ses release-groups (liste de mots-clés modifiable avec `--genres`).
   Une sortie taguée dans un autre genre est écartée ; une sortie sans tag hérite du genre de l'artiste.
6. **Éditions** : toutes les releases MusicBrainz (label, n° de catalogue, format, pays, code-barres) ; Discogs ajoute les compteurs
   « possédé / recherché » et les œuvres absentes de MusicBrainz (genre vérifié sur la fiche master/release Discogs).
7. **Labels liégeois** : catalogues des labels situés à Liège et tagués électro (les autres sont listés pour vérification).
8. **Score d'importance** : rééditions, diffusion internationale, notes et tags MusicBrainz, popularité Discogs, type de sortie,
   pondéré par la fiabilité du lien avec Liège (forte / moyenne / faible).

## Limites connues

- La qualité dépend des données communautaires : un artiste sans zone renseignée ni lien Discogs reste invisible.
- Les artistes MusicBrainz sans lien Discogs ne sont pas recherchés par nom sur Discogs (trop d'homonymes).
- La mention textuelle de Liège (description, profil Discogs) a une confiance « moyenne » : « label basé à Liège » suffit à la déclencher.
