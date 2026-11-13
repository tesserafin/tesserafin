# Règles pour les agents

Aucune compatibilité legacy à maintenir : personne n'utilise encore le produit. Les breaking
changes sont autorisés ; serveur, API, SDK et clients peuvent évoluer ensemble. Privilégier
l'architecture cible et supprimer les anciens chemins devenus inutiles — pas de détection
d'ancien serveur, de double comportement ni de couche de coexistence.
