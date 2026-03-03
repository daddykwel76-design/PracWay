Plugin CounterStrikeSharp pour CS2 permettant d'organiser des parcours "Practice" avec les joueurs présents sur le serveur.
Le plugin s'appellera "PracWay"

## Formats automatiques

Le format est choisi automatiquement selon le nombre de joueurs disponibles lors du lancement du parcours "Practice":

- 1 joueur: Parcours Practice solo en Terro vs 5 CT Bots
- 2 joueurs: Parcours Practice à 2 en Terros vs 5 CT Bots
- 3 joueurs: Parcours Practice à 3 en Terros vs 5 CT Bots
- 4 joueurs: Parcours Practice à 4 (3 en Terros vs 1 CT joueur + 4 CT Bots)
- 5 joueurs: Parcours Practice à 5 (3 en Terros vs 2 CT joueurs + 3 CT Bots)

## Enchaînement automatique des manches

-	La fin d'un parcours Practice se définit soit par la mort d'une équipe, soit au bout de 1 minute 30.
-	Ensuite le plugin relance automatiquement un nouveau parcours Practice avec tous les joueurs sauf si le même parcours est configuré (ceux qui sont morts au round précédent respawnent vivants à ce moment-là) 
- 	le prochain parcours Practice se lance sur **un autre parcours** s'il en existe plusieurs prêts (sauf configuration spéciales où un seul parcours Practice a été décidé)
-	Si 1,2 ou 3 joueurs présents, ils seront rassemblés dans la même équipe côté Terro et partiront des spawns définis comme a 1, a 2, a 3
-	Si 4 présents, 3 joueurs seront assignés Terro et 1 assigné CT avec les Bots
-	Si 5 présents, 3 joueurs seront assignés CT et 2 assignés Terro avec les Bots
-	A chaque nouveau parcours Practice les joueurs Terro seront positionnés sur les spawns a existants du parcours (ou b si 4 ou 5 joueurs présents)
-	Chaque CT sera identifié (CT1, CT2, CT3, CT4, CT5) avec plusieurs spawns possibles différents pour chacun Ex: CT1-1 CT1-2 CT1-3 CT1-4 CT1-5 // CT2-1 CT2-2 CT2-3 CT2-4 CT2-5 // etc. 
-	A chaque parcours practice, les Terros (BOTS) ou joueurs supplémentaires seront positionnés aléatoirement sur les spawns b existants et le nombre de Bots sera défini comme suit (12 bots si 1 ou 2 ou 3 joueurs présents, puis 5 	bots + 1 ou 2 joueurs)
-	Si un joueur se connecte au serveur pendant un parcours practice, il est automatiquement positionné en observateur jusqu'à la fin du mode parcours. Il pourra participer aux prochains parcours

## Configuration des parcours (obligatoire)

La configuration commence par la **création d'un parcours**, puis la définition de 3 spawns pour les joueurs Terros et 1 spawn minimum pour chaque CT**:

!way ouvre un menu_principal avec ces choix:
	!1 Lancer un parcours simple
Cela choisi le ou les joueurs qui rejoindront les CT Bots, quels CT Bots joueront et à quelles positions et sur quel parcours avec une quantité d'argent de départ (tout cela aléatoirement et différent à chaque début de parcours)

	!2 Lance un parcours spécial
ensuite ouvre un menu_sélection CT pour choisir le ou les joueurs qui rejoindront la team CT:
!1 Pseudo_joueur1
!2 Pseudo_joueur2
!3 Pseudo_joueur3
!4 Pseudo_joueur4
!5 Pseudo_joueur5

ouvre un menu_way avec le choix de parcours:
		!1 Nom_way1
		!2 Nom_way2
		!3 Nom_way3
		!4 Nom_way4
		!5 Etc...En fonction des parcours créés sur cette map
		!6 Différent à chaque début
ouvre un menu_rounds
!1 5 rounds
!2 10 rounds
!3 15 rounds
!4 20 rounds
!5 rounds illimités

	ensuite ouvre un menu_achat avec ces choix:

		!1 départ à 800$
		!2 départ à 2000$
		!3 départ à 2500$
		!4 départ à 3000$
		!5 départ à 3500$
		!6 départ à 4000$
		!7 départ à 4500$
		!8 départ à 6000$
		!9 somme aléatoire

Et lance la préparation des parcours avec le switch des joueurs dans la bonne équipe aux spawns par défaut de la carte en attendant la fin de la configuration
un menu_préparation des positions est proposé uniquement aux CT:
Message global: attente de la configuration CT

!x-x Choix aléatoire des CT et des positions
CT1
!1-1 Position double door accroupi
!1-2 Position Pit en décale toutes les 2 sec
!1-3 Position derrière la caisse
!1-4 etc...en fonction des spawns existants pour ce CT
!1-x Position choisie au hasard parmi ceux existants
CT2
!2-1 Position Car
!2-2 Rampe A
!2-x Position choisie au hasard parmi ceux existants
CT3
!3-1 etc.
!3-2
!3-x Position choisie au hasard parmi ceux existants
CT4
!4-1
!4-2
!4-x Position choisie au hasard parmi ceux existants
CT5
!5-1
!5-2
!5-x Position choisie au hasard parmi ceux existants
Nom_joueur1
!1 Position1
!2 Position2
!3 Position3
!4 Position4
!5 Position5
Nom_joueur2
!1 Position1
!2 Position2
!3 Position3
!4 Position4
!5 Position5

si un joueur CT, il doit choisir 4 CT qui seront utilisés pour le parcours avec sa position et sa propre position de départ (d'une manière générale le CT1 sera positionné au début du parcours, puis le CT2 un peu plus lin, etc.)
si 2 joueurs CT, choix de 3 CT uniquement et de la position et choix de la position des 2 joueurs CT

Et lancement des parcours les uns après les autres (le parcours doit être réussi par les Terros pour passer au suivant, sinon le même parcours est proposé, jusqu'à réussite) et indication de la commande !way_stop (retour au mode initial)

Puis lance la configuration de parcours choisi indéfiniment et indication de la commande !way_stop (retour au mode initial)

Lors de !way_stop  ou à la fin de chaque réussite de parcours ou échec de parcours indication du nombre de kills de chacun

	!3 Création de parcours
ouvre un menu_create_way avec ces choix
		!1 création d'un nouveau parcours
Message: taper !way "nom_parcours" pour créer un nouveau parcours(si nom_parcours existe déjà: message qui indique que ce nom est déjà créé et propose de taper un autre !way "nom_parcours")
Une fois le nom choisi, message validant la création de parcours et proposition de créer les spawns sur cette zone avec la commande !set <j1|j2|a|b> <1|2|3|etc.> <act1(action1_du_bot)|act2(action2_du_bot)|act3(action3_du_bot)|etc.
Claude.Ai: Propose moi quelles actions de bot sont possibles (debout, accroupi, en décale rapide illimitée (d'une position A vers B, puis retour A, tempo 2 sec. puis recommence), en décale accroupie, en mouvement d'un point à un autre (est-il possible d'enregistrer des mouvements que j'effectue pour que le bot les fassent ensuite à l'identique?), en décale unique lors de l'approche d'un ennemi, lance une flash et ensuite décale, etc...
(Les positions [x y z] [pitch] [yaw] [roll] sont définies par la position et l'orientation actuelle du joueur)
A chaque ajout de spawn: message qui indique le nombre restant à définir pour que le parcours soit valide (5 minimum pour a correspondant aux 5 CT bots, 1 minimum pour j1 (1joueur CT), 1 minimum pour j2 (2joueur CT) et 3 pour b correspondant aux Terros)
Lors de la creation d'un spawn joueur1_CT laser rouge, joueur2_CT laser orange, CT1_Bot laser vert, CT2_Bot laser jaune, etc. et Terro laser bleu.
Si tu as une solution pour distinguer par exemple: chaque laser vert du CT1_Bot aurait quelque chose de spécial pour voir quelle action a été assignée à cet endroit. cela serait génial!
Lors de la commande !set <j1|j2|a|b> <1|2|3|etc.> <act1(action1_du_bot)|act2(action2_du_bot)|act3(action3_du_bot)|etc. si la dernière variable n'est pas définie le bot aura comme action d'être debout en attente de tirer

Une fois les spawns minimums créés:
message qui indique que le parcours contient les spawns minimum pour être joué et possibilité de taper !fin pour sortir de la création de spawns (puis retour au menu_create_way) ou continuer de créer d'autres spawns
		!2 Modification ou ajout de spawns à un parcours existant
ouvre le menu_liste_parcours avec le choix des parcours existantes
			!1 Modification Nom_parcours1 avec indication du nombre de spawns existants pour chaque équipe
			!2 Modification Nom_Zparcours2 avec indication du nombre de spawns existants pour chaque équipe
			!3 etc... (en fonction des parcours existants)
Une fois le parcours choisi indication des spawns existants pour chaque équipe et indication de la commande !set <j1|j2|a|b> <1|2|3|etc.> <act1(action1_du_bot)|act2(action2_du_bot)|act3(action3_du_bot)|etc. pour modifier ou ajouter 
A chaque modification, indication des spawns existants et lasers visibles pour bien se rendre compte où ils sont positionnés et indication de la possibilité de sortir de la création/modification de ce parcours par la commande !fin (puis retour au menu_create_way)
		!3 suppression d'un parcours
ouvre un menu avec la liste des parcours existants puis choix et confirmation de la suppression
		!4 Retour au menu_principal
	!4 Sortir du menu_principal et retour au mode initial
	
!duel_way ouvre liste toutes les commandes possibles

## Sauvegarde des zones/spawns

Les noms de parcours et leurs spawns sont sauvegardés automatiquement **par map** à chaque création/suppression de parcours ou modification de spawn.

Chemin de sauvegarde:

- `configs/PracWay/zones/<nom_de_la_map>.json` (relatif au dossier d'exécution du serveur/plugin)

Au chargement de la map, le plugin recharge le fichier JSON correspondant à cette map.

#

