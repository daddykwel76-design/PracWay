Plugin CounterStrikeSharp pour CS2 permettant d'organiser des parcours "Practice" avec les joueurs présents sur le serveur.
Le plugin s'appellera "PracWay"

## Formats automatiques

Le format est choisi automatiquement selon le nombre de joueurs disponibles lors du lancement du parcours "Practice":

- 1 joueur: Parcours Practice solo en Terro vs 5 CT Bots
- 2 joueurs: Parcours Practice à 2 en Terros vs 5 CT Bots
- 3 joueurs: Parcours Practice à 3 en Terros vs 5 CT Bots

## Enchaînement automatique des manches

-	La fin d'un parcours Practice se définit soit par la mort d'une équipe, soit au bout de 1 minute 30.
-	Ensuite le plugin relance automatiquement un nouveau parcours Practice avec tous les joueurs sauf si le même parcours est configuré (ceux qui sont morts au round précédent respawnent vivants à ce moment-là) 
- 	le prochain parcours Practice se lance sur **un autre parcours** s'il en existe plusieurs prêts (sauf configuration spéciales où un seul parcours Practice a été décidé)
-	les joueurs présents seront rassemblés dans la même équipe côté Terro et partiront des spawns définis comme a 1, a 2, a 3
-	A chaque nouveau parcours Practice les joueurs Terro seront positionnés sur les spawns a existants du parcours
-	Chaque parcours aura 5 types de spawns nommés CT1, CT2, CT3, CT4, CT5.  Pour chaque type de spawns il sera possible de configurer différentes positions (ex: CT1-1 CT1-2 CT1-3 CT1-4 CT1-5 // CT2-1 CT2-2 CT2-3 CT2-4 CT2-5 // etc. 
-	A chaque parcours practice, les joueurs supplémentaires seront positionnés sur les spawns Terro prédéfinis et un Bot prendra au hasard un des spawns CT1, puis un autre fera de même avec les spawns CT2, etc.
-	Si un joueur se connecte au serveur pendant un parcours practice, il est automatiquement positionné en observateur jusqu'à la fin du mode parcours. Il pourra participer aux prochains parcours s'il reste une place (3 places maxi pour les joueurs)

## Configuration des parcours (obligatoire)

La configuration commence par la **création d'un parcours**, puis la définition de 3 spawns pour les joueurs Terros et 1 spawn minimum pour chaque type de spawns nommés CT1, CT2, etc.:

!way ouvre un menu_principal avec ces choix:
	!1 Lancer un parcours simple
Cela choisi un parcours au hasard, possitionnera les joueurs Terro sur leurs spawns et attribuera un Bot CT sur chaque type de spawn avec une quantité d'argent de départ

	!2 Lance un parcours spécial

ouvre un menu_way avec le choix de parcours:
		!w1 Nom_way1
		!w2 Nom_way2
		!w3 Nom_way3
		!w4 Nom_way4
		!w5 Etc...En fonction des parcours créés sur cette map
		!w6 Différent à chaque début
ouvre un menu_rounds
!w1 5 rounds
!w2 10 rounds
!w3 15 rounds
!w4 20 rounds
!w5 rounds illimités

	ensuite ouvre un menu_achat avec ces choix:

		!w1 départ à 800$
		!w2 départ à 2000$
		!w3 départ à 2500$
		!w4 départ à 3000$
		!w5 départ à 3500$
		!w6 départ à 4000$
		!w7 départ à 4500$
		!w8 départ à 6000$
		!w9 somme aléatoire

Et lance la préparation des parcours avec le switch des joueurs en terros aux spawns définis

Puis lancement des parcours les uns après les autres (le parcours doit être réussi par les Terros pour passer au suivant, sinon le même parcours est proposé, jusqu'à réussite) et indication de la commande !way_stop (retour au mode initial)

Puis lance la configuration de parcours choisi indéfiniment et indication de la commande !way_stop (retour au mode initial)

Lors de !way_stop  ou à la fin de chaque réussite de parcours ou échec de parcours indication du nombre de kills de chacun

	!3 Création de parcours
ouvre un menu_create_way avec ces choix
		!1 création d'un nouveau parcours
Message: taper !way_create "nom_parcours" pour créer un nouveau parcours (si nom_parcours existe déjà: message qui indique que ce nom est déjà créé et propose de taper un autre !way "nom_parcours")
Une fois le nom choisi, message validant la création de parcours et proposition de créer les spawns sur cette zone avec la commande !wset <a> <1|2|3> pour les spawns Terro puis !wset <CT1> <1|2|3> <act1(action1_du_bot)|act2(action2_du_bot)|act3(action3_du_bot)|etc.

(Les positions [x y z] [pitch] [yaw] [roll] sont définies par la position et l'orientation actuelle du joueur
A chaque ajout de spawn: message qui indique le nombre restant à définir pour que le parcours soit valide (3 pour a (Terros) et 1 minimum pour chaque type de spawn (CT1, CT2, etc.)
Lors de la creation d'un spawn des lasers de couleur seront créés pour bien identifier les positions.
Si la dernière variable n'est pas définie pour CT1, etc. le bot aura comme action d'être debout en attente de tirer

Une fois les spawns minimums créés:
message qui indique que le parcours contient les spawns minimum pour être joué et possibilité de taper !wfin pour sortir de la création de spawns (puis retour au menu_create_way) ou continuer de créer d'autres spawns
		!2 Modification ou ajout de spawns à un parcours existant
ouvre le menu_liste_parcours avec le choix des parcours existantes
			!1 Modification Nom_parcours1 avec indication du nombre de spawns existants pour chaque équipe
			!2 Modification Nom_Zparcours2 avec indication du nombre de spawns existants pour chaque équipe
			!3 etc... (en fonction des parcours existants)
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

