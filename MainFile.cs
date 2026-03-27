using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.Potions;
using System.Reflection;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace AutoPlay;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
	private const string ModId = "AutoPlay";

	private static Logger Logger { get; } = new(ModId, LogType.Generic);

	public static void Initialize()
	{
		Harmony harmony = new(ModId);
		harmony.PatchAll();
		Log("Initialized");
	}

	public static void Log(string message, LogLevel level = LogLevel.Info, int skipFrames = 1)
	{
		Logger.LogMessage(level, $"[{ModId}] {message}", skipFrames);

		try
		{
			var console = NDevConsole.Instance;
			var outputBufferField =
				typeof(NDevConsole).GetField("_outputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
			if (outputBufferField?.GetValue(console) is not RichTextLabel outputBuffer) return;
			outputBuffer.Text += $"[color=#00ffff][{ModId}][/color] {message}";
			outputBuffer.Text += "\n";
		}
		catch
		{
			// Console might not be initialized yet
		}
	}
}

[HarmonyPatch]
public class Patch
{
	[HarmonyPrefix]
	[HarmonyPatch(typeof(NMouseCardPlay), "TargetSelection")]
	private static bool TargetSelection(NMouseCardPlay __instance, TargetMode targetMode, ref Task __result)
	{
		var card = AccessTools.PropertyGetter(typeof(NCardPlay), "Card")?.Invoke(__instance, null) as CardModel;

		if (!IsAutoPlayable(card)) return true;

		// manually set _target if type == AnyEnemy
		if (card is { TargetType: TargetType.AnyEnemy })
		{
			var target = card.CombatState?.HittableEnemies[0];
			if (target is null) return true;
			AccessTools.Field(typeof(NMouseCardPlay), "_target").SetValue(__instance, target);
		}

		// MegaCrit sets _target for All other types in IsAutoPlayable()
		__result = Task.CompletedTask;
		return false;
	}


	[HarmonyPrefix]
	[HarmonyPatch(typeof(NMouseCardPlay), "IsCardInPlayZone")]
	private static bool IsCardInPlayZone(ref bool __result)
	{
		__result = true;
		return false;
	}

	[HarmonyPatch(typeof(NPlayerHand), "StartCardPlay")]
	private static void StartCardPlay(NHandCardHolder holder, ref bool startedViaShortcut)
	{
		if (IsAutoPlayable(holder.CardModel)) startedViaShortcut = true;
	}

	[HarmonyPatch(typeof(NHandCardHolder), "OnFocus")]
	[HarmonyPostfix]
	private static void CardOnFocus(NHandCardHolder __instance)
	{
		if(IsAutoPlayable(__instance.CardModel))
		{
			var target = __instance.CardModel?.CombatState?.HittableEnemies[0];
			if (target is null) return;

			__instance.CardNode?.SetPreviewTarget(target);
		}        
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(NPotionHolder), "UsePotion")]
	private static bool UsePotion(NPotionHolder __instance, ref Task __result)
	{
		PotionModel? potion = __instance.Potion?.Model;
		if (potion == null) return true;

		if (!IsAutoPlayable(potion)) return true;

		var target = potion.Owner.Creature.CombatState?.HittableEnemies[0];
		if (target is null) return true;

		potion.EnqueueManualUse(target);

		__result = Task.CompletedTask;
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(WellLaidPlansPower), "BeforeFlushLate")]
	private static bool BeforeFlushLate(WellLaidPlansPower __instance, PlayerChoiceContext choiceContext, Player player, ref Task __result)
	{
		if (player != __instance.Owner.Player)
		{
			return true;
		}

		CardPile handPile = PileType.Hand.GetPile(__instance.Owner.Player);

		if (handPile.Cards.Count == 0) return true;
		else if (handPile.Cards.Count <= __instance.Amount)//if the number of remaining cards is same or lower than required amount to choose, choose them all automatically, unless they are unplayable
		{
			foreach (CardModel item in handPile.Cards)
			{
				if (item.Type <= CardType.Power)
				{
					item.GiveSingleTurnRetain();
				}
			}

			__result = Task.CompletedTask;
			return false;
		}

		CardModel first = handPile.Cards[0];

		for (int th = 0; th < handPile.Cards.Count; th++)
		{
			if (!CardsEqual(first, handPile.Cards[th]))
			{
				return true;
			}
		}

		//if all remaining cards are identical, it will retain required amount automatically

		int num_to_retain = __instance.Amount;

		for (int th = 0; th < handPile.Cards.Count && num_to_retain > 0; th++)
		{
			CardModel card = handPile.Cards[th];
			if (card.Type <= CardType.Power)
			{
				card.GiveSingleTurnRetain();
				num_to_retain--;
			}
		}

		__result = Task.CompletedTask;
		return false;
	}

	[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
	[HarmonyPostfix]
	private static void AfterCardPlayed(CombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
	{
		if (cardPlay.Card == null || cardPlay.Card.Owner == null || cardPlay.Card.CombatState == null || cardPlay.Card.Owner.PlayerCombatState == null) return;

		int num_enemies_survive_this_turn = 0, num_enemies_intends_attack = 0, num_enemies_vulnerable_next_round = 0;

		IReadOnlyList<Creature> enemies = cardPlay.Card.CombatState.Enemies;
		for (int th = 0; th < enemies.Count; th++)
		{
			Creature enemy = enemies[th];
			//Log.Info(th + ". enemy: " + enemy.Name + " hp: " + enemy.CurrentHp + " poison: " + enemy.GetPowerAmount<PoisonPower>());
			if (enemy.IsAlive && (enemy.GetPowerAmount<ArtifactPower>() > 0 || enemy.GetPowerAmount<InfestedPower>() > 0 || enemy.GetPowerAmount<SteamEruptionPower>() > 0 || enemy.GetPowerAmount<PoisonPower>() < enemy.CurrentHp))
			{
				num_enemies_survive_this_turn++;
				if (enemy.Monster?.IntendsToAttack == true)
				{
					num_enemies_intends_attack++;
				}
				if(enemy.GetPowerAmount<VulnerablePower>() > 1)
				{
					num_enemies_vulnerable_next_round++;
				}
			}
		}

		CardPile handPile = PileType.Hand.GetPile(cardPlay.Card.Owner);

		if(num_enemies_survive_this_turn == 0)//no enemies will survive after this card was played
		{
			int num_damage_received_from_cards = 0;
			bool has_feed_card = false;

			for (int th = 0; th < handPile.Cards.Count; th++)
			{
				CardModel card = handPile.Cards[th];
				if (card.Title == "Feed")
				{
					has_feed_card = true;
				}
				else if (card.Type > CardType.Power)
				{
					num_damage_received_from_cards += card.DynamicVars.Damage.IntValue;
				}
			}

			if (has_feed_card == false && num_damage_received_from_cards <= cardPlay.Card.Owner.Creature.Block + cardPlay.Card.Owner.Creature.GetPowerAmount<PlatingPower>())
			{
				//we either have no damage debuffs or we can block them so no reason to play this round further
				PlayerCmd.EndTurn(cardPlay.Card.Owner, true);
				return;
			}
		}

		int num_playable_cards = 0, num_cards_using_energy = 0, minimum_energy_needed_to_play_card = 99;

		for (int th = 0; th < handPile.Cards.Count; th++)
		{
			CardModel card = handPile.Cards[th];
			int card_star_cost = card.GetStarCostWithModifiers();
			int card_energy_cost = card.EnergyCost.GetWithModifiers(CostModifiers.All);
			//Log.Info(th + ". card: " + card.Title + " card_type: " + card.Type + " card_star_cost: " + card_star_cost + ", card_energy_cost: " + card_energy_cost);

			UnplayableReason reason;
			AbstractModel preventer;

			if (card.CanPlay(out reason, out preventer))
			{
				return;
			}
			else if (card.Type <= CardType.Power)
			{
				num_playable_cards++;
			}
			if(card_energy_cost > 0 && reason == UnplayableReason.EnergyCostTooHigh)
			{
				num_cards_using_energy++;
				if (card_star_cost < minimum_energy_needed_to_play_card)
				{
					minimum_energy_needed_to_play_card = card_star_cost;
				}
			}
		}

		CardPile drawPile = PileType.Draw.GetPile(cardPlay.Card.Owner);

		//if we got here, we have no cards to play or no energy to play them
		foreach (PotionModel potion in cardPlay.Card.Owner.Potions)
		{
			string name = potion.Title.GetRawText();
			if (name == "Gigantification Potion")//if we have no playable attack cards in hand, then potions that improve attack cards are useless
			{
				//ignore
			}
			else if (name != "Strength Potion" && (name == "Dexterity Potion" || name == "Flex Potion" || name == "Duplicator" || name == "Fysh Oil" || name == "Speed Potion"))//if we can't play any card, then potions that adds Strength or Dexterity are useless
			{
				//ignore
			}
			else if (cardPlay.Card.Owner.Creature.Block <= 0 && (name == "Fortifier"))//if we have no block, then potions that multiply block are useless
			{
				//ignore
			}
			else if (num_enemies_intends_attack == 0 && (name == "Block Potion" || name == "Shackling Potion" || name == "Potion of Binding" || name == "Weak Potion"))//if no enemy intends to attack
			{
				//ignore
			}
			if(num_enemies_vulnerable_next_round >= num_enemies_survive_this_turn && (name == "Vulnerable Potion" || name == "Fear Potion"))//if all enemies will be vulnerable next round too, then potions that adds vulnerable are useless
			{
				//ignore
			}
			else if (cardPlay.Card.Owner.Creature.CurrentHp == cardPlay.Card.Owner.Creature.MaxHp && (name == "Blood Potion" || name == "Regen Potion"))//if we have maximum HP, then potions that heals are useless
			{
				//ignore
			}
			else if (handPile.Cards.Count == 0 && (name == "Ashwater" || name == "Bottled Potential" || name == "Gambler's Brew"))//if we have no cards in hand, then potions that does something with cards in hand are useless
			{
				//ignore
			}
			else if (num_cards_using_energy == 0 && minimum_energy_needed_to_play_card <= 2 && name == "Energy Potion")//if we have no cards in hand that uses energy or only cards with higher energy cost than we can get, then potions that gives us energy are useless
			{
				//ignore
			}
			else if (drawPile.Cards.Count == 0 && name == "Distilled Chaos")//if we have no cards in draw pile, then potions that plays cards from draw pile are useless
			{
				//ignore
			}
			else if (num_playable_cards == 0 && (name == "Stable Serum" || name == "Blessing of the Forge" || name == "Duplication Potion"))//if we have no playable cards in hand, then potions that retains or improve cards in hand are useless
			{
				//ignore
			}
			else
			{
				return;//if we get here we have some potion that can still benefit us this turn
			}
		}

		//if it falls here, we have no cards to play at all and no potions which would have sense to use, then end turn automatically
		PlayerCmd.EndTurn(cardPlay.Card.Owner, true);     
	}

	[HarmonyPatch(typeof(Creature), nameof(Creature.AfterTurnStart))]
	[HarmonyPostfix]
	private static void AfterTurnStart(Creature __instance, int roundNumber, CombatSide side)
	{
		if (__instance.CombatState != null && __instance.IsPlayer && __instance.Player != null && roundNumber > 0 && side != CombatSide.Enemy)
		{
			int num_enemies_survive_this_turn = 0;

			IReadOnlyList<Creature> enemies = __instance.CombatState.Enemies;
			for (int th = 0; th < enemies.Count; th++)
			{
				Creature enemy = enemies[th];
				//Log.Info(th + ". enemy: " + enemy.Name + " hp: " + enemy.CurrentHp + " poison: " + enemy.GetPowerAmount<PoisonPower>());
				if (enemy.IsAlive && (enemy.GetPowerAmount<ArtifactPower>() > 0 || enemy.GetPowerAmount<InfestedPower>() > 0 || enemy.GetPowerAmount<SteamEruptionPower>() > 0 || enemy.GetPowerAmount<PoisonPower>() < enemy.CurrentHp))
				{
					num_enemies_survive_this_turn++;
				}
			}

			CardPile handPile = PileType.Hand.GetPile(__instance.Player);

			if (num_enemies_survive_this_turn == 0)//no enemies will survive after this card was played
			{
				int num_damage_received_from_cards = 0;
				bool has_feed_card = false;

				for (int th = 0; th < handPile.Cards.Count; th++)
				{
					CardModel card = handPile.Cards[th];
					if(card.Title == "Feed")
					{
						has_feed_card = true;
					}
					else if (card.Type > CardType.Power)
					{
						num_damage_received_from_cards += card.DynamicVars.Damage.IntValue;
					}
				}

				if (has_feed_card == false && num_damage_received_from_cards <= __instance.Block + __instance.GetPowerAmount<PlatingPower>())
				{
					//we either have no damage debuffs or we can block them so no reason to play this round further
					PlayerCmd.EndTurn(__instance.Player, true);
					return;
				}
			}
		}
	}

	[HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards))]
	public static class Patch_SelectCards
	{
		[HarmonyPrefix]
		private static bool Prefix(NPlayerHand __instance, CardSelectorPrefs prefs, Func<CardModel, bool> filter, AbstractModel source, NPlayerHand.Mode mode, ref Task<IEnumerable<CardModel>> __result)
		{
			if (ModState.CurrentPlayer == null) return true;

			CardPile handPile = PileType.Hand.GetPile(ModState.CurrentPlayer);

			if (handPile.Cards.Count == 0) return true;//if we have no cards, game already selects nothing by default, so we can leave it to original function
			else if (prefs.MaxSelect == 999999999) return true;//Gambler's Brew potion

			var selected = new List<CardModel> { };

			if (handPile.Cards.Count <= prefs.MinSelect)
			{
				for (int th = 0; th < handPile.Cards.Count; th++)
				{
					selected.Add(handPile.Cards[th]);
				}
			}
			else
			{
				int num_cards_to_discard_optimally = 0;
				for (int th = 0; th < handPile.Cards.Count; th++)
				{
					CardModel card = handPile.Cards[th];
					if (card.IsSlyThisTurn || card.Type > CardType.Power)
					{
						num_cards_to_discard_optimally++;
					}
				}

				if (num_cards_to_discard_optimally == prefs.MinSelect)//if there is only one Sly or unplayable card it will discard it automatically - should be always the most optimal choice
				{
					for (int th = 0; th < handPile.Cards.Count && num_cards_to_discard_optimally > 0; th++)
					{
						CardModel card = handPile.Cards[th];
						if (card.IsSlyThisTurn || card.Type > CardType.Power)
						{
							selected.Add(card);
							num_cards_to_discard_optimally--;
						}
					}
				}
				else
				{
					CardModel first = handPile.Cards[0];

					for (int th = 0; th < handPile.Cards.Count; th++)
					{
						if (!CardsEqual(first, handPile.Cards[th]))
						{
							return true;
						}
					}

					for (int th = 0; th < prefs.MinSelect; th++)
					{
						selected.Add(handPile.Cards[th]);
					}
				}
			}

			__result = Task.FromResult<IEnumerable<CardModel>>(selected);
			return false;
		}
	}

	[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand))]
	[HarmonyPrefix]
	private static void FromHand(PlayerChoiceContext context, Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
	{
		ModState.CurrentPlayer = player;
	}

	public static bool IsAutoPlayable(CardModel? card)
	{
		if (card?.CombatState == null) return false;

		return card.TargetType switch
		{
			TargetType.None or TargetType.Self or TargetType.AllEnemies or TargetType.RandomEnemy => true,
			TargetType.AnyEnemy => card.CombatState.HittableEnemies.Count == 1,
			_ => false
		};
	}

	public static bool IsAutoPlayable(PotionModel? potion)
	{
		if (potion?.Owner.Creature.CombatState == null) return false;

		return potion.TargetType switch
		{
			TargetType.None or TargetType.Self or TargetType.AllEnemies or TargetType.RandomEnemy => true,
			TargetType.AnyEnemy => potion.Owner.Creature.CombatState.HittableEnemies.Count == 1,
			_ => false
		};
	}

	public static bool CardsEqual(CardModel? cardA, CardModel? cardB)
	{
		if (cardA == null || cardB == null) return false;
		if (cardA.Type != cardB.Type) return false;
		else if (cardA.Type != cardB.Type) return false;
		else if (cardA.Rarity != cardB.Rarity) return false;
		else if (cardA.Title != cardB.Title) return false;
		else if (cardA.CurrentUpgradeLevel != cardB.CurrentUpgradeLevel) return false;
		if (cardA.Enchantment == null && cardB.Enchantment != null) return false;
		else if(cardA.Enchantment != null && cardB.Enchantment != null)
		{
			if (cardA.Enchantment.Title.LocEntryKey != cardB.Enchantment.Title.LocEntryKey)
			{
				return false;
			}
		}
		if (cardA.Affliction == null && cardB.Affliction != null) return false;
		else if (cardA.Affliction != null && cardB.Affliction != null)
		{
			if (cardA.Affliction.Title.LocEntryKey != cardB.Affliction.Title.LocEntryKey)
			{
				return false;
			}
		}
		return true;
	}

	public static class ModState
	{
		public static Player? CurrentPlayer;
	}
}
