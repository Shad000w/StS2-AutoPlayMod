# AutoPlay

This modification attempts to remove all extra steps when there is no reason to wait for player's input.

## Features

### 1. Attack cards are played automatically when there is just one enemy. 

Additionally, when the attack card is hovered and only one enemy remains, it will show updated damage value as if you would drag the card on top of the enemy in vanilla without clicking.

### 2. Offensive potions are thrown automatically when there is just one enemy.

### 3. Potions are used automatically during combat.

With exception of Foul Potion where player still gets the default choice between use and discard. All other potions are automatically used when clicked at.

### 4. Player turn is ended automatically if:
- all enemies will die after turn ends due to the poison AND player doesn't have Feed card AND player doesn't have damaging debuff cards OR player block+plating is same or higher than what he would receive from these cards
- player has no cards to play anymore (either no cards in hand or not enough energy/stars to play them)
- player has no potion that would have sense to use (this is hardcoded to exact potion names, their effects, eg. Strength or Dexterity Potion has no sense when we can't play any more cards this round and so on)

### 5. Retain/Discard selection doesn't need to be confirmed.

If you choose required amount of cards the selection will end automatically, so make up your mind before you click!

### 6. Cards are selected automatically if player is asked to discard during combat if:
- the number of cards in hand is same or less than amount to discard
- there is exactly the same amount of sly+unplayable cards in player's hand as the amount to discard
- number of sly+unplayable cards in players's hand is greater than amount to discard, but they are identical
- all remaining cards in hand are identical

### 7. Before Flush Late retain selection selects cards automatically if:
- player has no playable cards (in this case it selects nothing, this is allowed to do in vanilla)
- player has less or the exact amount of playable cards than we are asked to retain
- all remaining cards in hand are identical

### 8. Any other card selection during combat also selects cards automaically if:
- all cards in selection are equal

This type of selection is triggered from various cards for various purposes so there is no other condition to use and in this exact case (unlike discard) game selects automatically if the number of cards in selection is same or lesser than required amount.

## Disclaimer ##

Despite the mod’s name, its goal is not to play the game for the player. There are situations where all remaining cards could be played automatically, for example, when only identical cards remain, but this is not the intention of this mod. Instead, it aims to remove unnecessary steps, such as prompts like “Choose 1 of 1,” which add no meaningful gameplay value.

## Known Issues

This mod was only tested in single player mode under Windows OS. Might not work under Unix/Mac or in multiplayer.

Also I am not an expert player of StS2, there are definitely a situations that I didn't cover or on the opposite a situations where this modification might be unoptimal. Create an issue if you run into any such situation please.