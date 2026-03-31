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
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace AutoPlay;

public static class ModSettings
{
	public static bool AutomaticDiscard { get; set; } = false;
	public static bool AutomaticSelect { get; set; } = false;
	public static bool AutomaticRetain { get; set; } = false;
}
public class AutoPlayConfigData
{
	public bool AutomaticDiscard { get; set; }
	public bool AutomaticSelect { get; set; }
	public bool AutomaticRetain { get; set; }
}

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
	private const string ModId = "AutoPlay";

	private static Logger Logger { get; } = new(ModId, LogType.Generic);

	public static void Initialize()
	{
		Harmony harmony = new(ModId);
		harmony.PatchAll();

		if (File.Exists("mods/AutoPlay.json"))
		{
			var json = File.ReadAllText("mods/AutoPlay.json");
			var data = JsonSerializer.Deserialize<AutoPlayConfigData>(json);

			if (data != null)
			{
				ModSettings.AutomaticDiscard = data.AutomaticDiscard;
				ModSettings.AutomaticSelect = data.AutomaticSelect;
				ModSettings.AutomaticRetain = data.AutomaticRetain;
			}
		}
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
    [HarmonyPatch(typeof(NPotionHolder), "OnRelease")]
    private static bool OnPotionClicked(NPotionHolder __instance)
    {
        if (__instance?.Potion?.Model.Owner.Creature.CombatState == null || CombatManager.Instance.IsOverOrEnding) return true;

        var isUsable_field = AccessTools.Field(typeof(NPotionHolder), "_isUsable");

        var isUsable_value = isUsable_field.GetValue(__instance);

        if (isUsable_value == null || (bool)isUsable_value == false) return true;

        if (__instance.Potion.Model.Title.GetRawText() != "Foul Potion")//automatically use every potion except Foul Potion when clicked at them in combat since there is no reason to discard
        {
            __instance.UsePotion();
            return false;
        }
        return true;
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
		if (!ModSettings.AutomaticRetain) return true;
		if (player != __instance.Owner.Player) return true;

		CardPile handPile = PileType.Hand.GetPile(__instance.Owner.Player);

		if (handPile.Cards.Count == 0) return true;
		else if (handPile.Cards.Count <= __instance.Amount)//if the number of remaining cards is same or lower than required amount to choose, choose them all automatically, unless they are unplayable
		{
			foreach (CardModel item in handPile.Cards)
			{
				if (item.Type <= CardType.Power && !item.Keywords.Contains(CardKeyword.Retain))
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
			if (card.Type <= CardType.Power && !card.Keywords.Contains(CardKeyword.Retain))
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
	private static void AfterCardPlayed(CardPlay cardPlay)
	{
		if (CombatManager.Instance.IsPlayPhase)
		{
			AutomaticEndTurn(cardPlay.Card.Owner);
		}			
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionUsed))]
    [HarmonyPostfix]
    private static void AfterPotionUsed(PotionModel potion) 
	{
        AutomaticEndTurn(potion.Owner);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionDiscarded))]
    [HarmonyPostfix]
    private static void AfterPotionDiscarded(PotionModel potion)
    {
        AutomaticEndTurn(potion.Owner);
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
				int damage_from_poison = (enemy.GetPower<PoisonPower>()?.CalculateTotalDamageNextTurn() ?? 0);

				if (enemy.IsAlive && (enemy.GetPowerAmount<ArtifactPower>() > 0 || enemy.GetPowerAmount<InfestedPower>() > 0 || enemy.GetPowerAmount<SteamEruptionPower>() > 0 || enemy.GetPowerAmount<AdaptablePower>() > 0 || damage_from_poison < enemy.CurrentHp))
				{
					num_enemies_survive_this_turn++;
				}
			}

			CardPile handPile = PileType.Hand.GetPile(__instance.Player);

			if (num_enemies_survive_this_turn == 0)//no enemies will survive after turn started
			{
				int num_damage_received_from_cards = 0;
				bool has_feed_card = false;

				for (int th = 0; th < handPile.Cards.Count; th++)
				{
					CardModel card = handPile.Cards[th];
					if(card.Id.Entry == "FEED")
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
			if (!ModState.IsDiscardTypeOfSelect && !ModSettings.AutomaticSelect) return true;
			if (ModState.IsDiscardTypeOfSelect && !ModSettings.AutomaticDiscard) return true;
			if (ModState.CurrentPlayer == null) return true;

			CardPile handPile = PileType.Hand.GetPile(ModState.CurrentPlayer);

			if (handPile.Cards.Count == 0) return true;//if we have no cards, game already selects nothing by default, so we can leave it to original function
			else if (prefs.MaxSelect == 999999999) return true;//Gambler's Brew potion
			else if (prefs.RequireManualConfirmation || (!ModState.IsDiscardTypeOfSelect && prefs.MinSelect == 0)) return true;

			var selected = new List<CardModel> { };

			if (handPile.Cards.Count <= prefs.MinSelect)//if we have less cards than amount needed to select, select them automatically
			{
				for (int th = 0; th < handPile.Cards.Count; th++)
				{
					selected.Add(handPile.Cards[th]);
				}
			}
			else if (ModState.IsDiscardTypeOfSelect)//discard selection
			{
				CardModel? first_optimal_card__to_discard = null;
				bool all_cards_to_discard_optimally_identical = true;
				int num_cards_to_discard_optimally = 0;
				for (int th = 0; th < handPile.Cards.Count; th++)
				{
					CardModel card = handPile.Cards[th];
					if (card.IsSlyThisTurn || card.Type > CardType.Power)
					{
						num_cards_to_discard_optimally++;
						if (first_optimal_card__to_discard == null) first_optimal_card__to_discard = card;
						else if(!CardsEqual(first_optimal_card__to_discard, card))
						{
							all_cards_to_discard_optimally_identical = false;
						}
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
				else if(num_cards_to_discard_optimally > prefs.MinSelect && all_cards_to_discard_optimally_identical)//if all optimally discardable cards are same, discard required amount automatically
				{
					int num_to_discard = prefs.MinSelect;
					for (int th = 0; th < handPile.Cards.Count && num_to_discard > 0; th++)
					{
						CardModel card = handPile.Cards[th];
						if (card.IsSlyThisTurn || card.Type > CardType.Power)
						{
							selected.Add(card);
							num_to_discard--;
						}
					}
				}
				else
				{
					CardModel first = handPile.Cards[0];

					for (int th = 1; th < handPile.Cards.Count; th++)
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
			else//either retain or exhaust selection - only select automatically when all cards in hand are identical
			{
				CardModel first = handPile.Cards[0];
				for (int th = 1; th < handPile.Cards.Count; th++)
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

			__result = Task.FromResult<IEnumerable<CardModel>>(selected);
			return false;
		}
	}

	[HarmonyPatch(typeof(NPlayerHand), "SelectCardInSimpleMode")]
	[HarmonyPostfix]
	private static void SelectCardInSimpleMode(NPlayerHand __instance)
	{
		var prefsField = Traverse.Create(__instance).Field<CardSelectorPrefs>("_prefs");
		if (prefsField == null) return;
		var prefs = prefsField.Value;

		var selectedCards = Traverse.Create(__instance).Field<List<CardModel>>("_selectedCards").Value;
		int selectedCount = selectedCards?.Count ?? 0;

		if (prefs.MaxSelect < 1) return;
		else if (selectedCards == null || selectedCount != prefs.MaxSelect) return;

		// Press the confirm button to complete the selection
		var confirmMethod = AccessTools.Method(__instance.GetType(), "OnSelectModeConfirmButtonPressed");
		if (confirmMethod != null)
		{
			confirmMethod.Invoke(__instance, new object[] { null! });
		}
	}

	[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand))]
	[HarmonyPrefix]
	private static void FromHand(PlayerChoiceContext context, Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
	{
		ModState.CurrentPlayer = player;
	}

	[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForDiscard))]
	[HarmonyPrefix]
	private static void BeforeFromHandForDiscard(PlayerChoiceContext context, Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
	{
		ModState.IsDiscardTypeOfSelect = true;
	}

	[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForDiscard))]
	[HarmonyPostfix]
	private static void AfterFromHandForDiscard(PlayerChoiceContext context, Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
	{
		ModState.IsDiscardTypeOfSelect = false;
	}

	[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid))]
	[HarmonyPrefix]
	private static bool FromSimpleGrid(PlayerChoiceContext context, IReadOnlyList<CardModel> cardsIn, Player player, CardSelectorPrefs prefs, ref Task<IEnumerable<CardModel>> __result)
	{
		if (!ModSettings.AutomaticDiscard) return true;
		if (CombatManager.Instance.IsEnding) return true;

		List<CardModel> cards = cardsIn.ToList();
		if (!prefs.RequireManualConfirmation && cards.Count <= prefs.MinSelect)//if we have less cards than needed to select fall back to original function (it selects automatically)
		{
			return true;
		}

		CardModel first = cards[0];
		for (int th = 1; th < cards.Count; th++)
		{
			if (!CardsEqual(first, cards[th]))
			{
				return true;
			}
		}

		//if all remaining cards are identical, it will select required amount automatically
		var selected = new List<CardModel> { };
		for (int th = 0; th < prefs.MinSelect; th++)
		{
		selected.Add(cards[th]);
		}

		__result = Task.FromResult<IEnumerable<CardModel>>(selected);
		return false;
	}

	private static void AutomaticEndTurn(Player player)
	{
		if (player.Creature.CombatState == null) return;

        int num_enemies_survive_this_turn = 0, num_enemies_intends_attack = 0, minimum_enemy_hitpoints = 999;

        IReadOnlyList<Creature> enemies = player.Creature.CombatState.Enemies;
        for (int th = 0; th < enemies.Count; th++)
        {
            Creature enemy = enemies[th];
            int damage_from_poison = (enemy.GetPower<PoisonPower>()?.CalculateTotalDamageNextTurn() ?? 0);

            if (enemy.IsAlive && (enemy.GetPowerAmount<ArtifactPower>() > 0 || enemy.GetPowerAmount<InfestedPower>() > 0 || enemy.GetPowerAmount<SteamEruptionPower>() > 0 || enemy.GetPowerAmount<AdaptablePower>() > 0 || damage_from_poison < enemy.CurrentHp))
            {
                num_enemies_survive_this_turn++;

                if (enemy.Monster?.IntendsToAttack == true)
                {
                    num_enemies_intends_attack++;
                }
                int hp_after_poison = enemy.CurrentHp - damage_from_poison - enemy.GetPowerAmount<PlowPower>() + enemy.Block;
                if (hp_after_poison < minimum_enemy_hitpoints)
                {
                    minimum_enemy_hitpoints = hp_after_poison;
                }
            }
        }

        CardPile handPile = PileType.Hand.GetPile(player);

        if (num_enemies_survive_this_turn == 0)//no enemies will survive after this card was played
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

            if (has_feed_card == false && num_damage_received_from_cards <= player.Creature.Block + player.Creature.GetPowerAmount<PlatingPower>())
            {
                //we either have no damage debuffs or we can block them so no reason to play this round further
                PlayerCmd.EndTurn(player, true);
                return;
            }
        }

        int num_playable_cards = 0, num_upgradable_cards = 0, num_cards_using_energy = 0, num_cards_using_stars = 0, minimum_energy_needed_to_play_card = 99, minimum_stars_needed_to_play_card = 99;

        for (int th = 0; th < handPile.Cards.Count; th++)
        {
            CardModel card = handPile.Cards[th];
            int card_star_cost = card.GetStarCostWithModifiers();
            int card_energy_cost = card.EnergyCost.GetWithModifiers(CostModifiers.All);
            //Log.Info(th + ". card: " + card.Title + " card_type: " + card.Type + " card_star_cost: " + card_star_cost + ", card_energy_cost: " + card_energy_cost);

            if (card.CanPlay(out UnplayableReason reason, preventer : out _))
            {
                return;
            }
            else if (card.Type <= CardType.Power)
            {
                num_playable_cards++;
            }
            if(card.IsUpgradable)
            {
                num_upgradable_cards++;
            }
            if (card_energy_cost > 0 && reason == UnplayableReason.EnergyCostTooHigh)
            {
                num_cards_using_energy++;
                if (card_energy_cost < minimum_energy_needed_to_play_card)
                {
                    minimum_energy_needed_to_play_card = card_energy_cost;
                }
            }
            if (card_star_cost > 0 && reason == UnplayableReason.StarCostTooHigh)
            {
                num_cards_using_stars++;
                if (card_star_cost < minimum_stars_needed_to_play_card)
                {
                    minimum_stars_needed_to_play_card = card_star_cost;
                }
            }
        }

        CardPile drawPile = PileType.Draw.GetPile(player);
        CardPile discardPile = PileType.Discard.GetPile(player);
        int total_damage_from_potions = 0;

        //if we got here, we have no cards to play or no energy to play them
        foreach (PotionModel another_potion in player.Potions)
        {
			string id = another_potion.Id.Entry;
            if (id == "GIGANTIFICATION_POTION" || id == "SOLDIERS_STEW")//if we have no playable attack cards in hand, then potions that improve attack cards are useless
            {
                //ignore
            }
            else if (another_potion.TargetType == TargetType.AnyPlayer && (another_potion.DynamicVars.ContainsKey("StrengthPower") || another_potion.DynamicVars.ContainsKey("DexterityPower")))//if we can't play any card, then potions that adds Strength or Dexterity are useless
            {
                //ignore
            }
            else if (another_potion.DynamicVars.ContainsKey("VulnerablePower"))//if we can't play any card, then potions that adds Vulnerability are useless
            {
                //ignore
            }
            else if (player.Creature.Block <= 0 && id == "FORTIFIER")//if we have no block, then potions that multiply block are useless
            {
                //ignore
            }
            else if (num_enemies_intends_attack == 0 && (another_potion.DynamicVars.ContainsKey("Block") || another_potion.DynamicVars.ContainsKey("PlatingPower")))//if no enemy intends to attack, then potions that adds block or plating are useless
            {
                //ignore
            }
            else if (num_enemies_intends_attack == 0 && (another_potion.DynamicVars.ContainsKey("WeakPowername") || another_potion.DynamicVars.ContainsKey("DamageDecrease")))//if no enemy intends to attack, then potions that weakens enemy are useless
            {
                //ignore
            }
            else if (num_enemies_intends_attack == 0 && (another_potion.TargetType == TargetType.AllEnemies || another_potion.TargetType == TargetType.AnyEnemy || another_potion.TargetType == TargetType.RandomEnemy) && another_potion.DynamicVars.ContainsKey("StrengthPower"))//if no enemy intends to attack, then potions that reduces enemy Strength are useless
            {
                //ignore
            }            
            else if (player.Creature.CurrentHp >= player.Creature.MaxHp && (another_potion.DynamicVars.ContainsKey("MaxHp") || another_potion.DynamicVars.ContainsKey("HealPercent") || another_potion.DynamicVars.ContainsKey("RegenPower")))//if we have maximum HP, then potions that modify HPs are useless
            {
                //ignore
            }
            else if (another_potion.DynamicVars.ContainsKey("Energy") && minimum_energy_needed_to_play_card > another_potion.DynamicVars.Energy.IntValue)//if we have no cards in hand that uses energy or only cards with higher energy cost than we can get, then potions that gives us energy are useless
            {
                //ignore
            }
            else if (another_potion.DynamicVars.ContainsKey("Stars") && minimum_stars_needed_to_play_card > another_potion.DynamicVars.Stars.IntValue)//if we have no cards in hand that uses stars or only cards with higher stars cost than we can get, then potions that gives us stars are useless
            {
                //ignore
            }
            else if (handPile.Cards.Count == 0 && (id == "ASHWATER" || id == "BOTTLED_POTENTIAL" || id == "GAMBLERS_BREW"))//if we have no cards in hand, then potions that does something with cards in hand are useless
            {
                //ignore
            }
            else if (drawPile.Cards.Count == 0 && (id == "DISTILLED_CHAOS" || id == "DROPLET_OF_PRECOGNITION"))//if we have no cards in draw pile, then potions that plays cards from draw pile are useless
            {
                //ignore
            }
            else if (discardPile.Cards.Count == 0 && id == "LIQUID_MEMORIES")//if we have no cards in discard pile, then potions that plays cards from discard pile are useless
            {
                //ignore
            }
            else if (num_playable_cards == 0 && (id == "STABLE_SERUM" || id == "BLESSING_OF_THE_FORGE" || id == "DUPLICATOR"))//if we have no playable cards in hand, then potions that retains or improve cards in hand are useless
            {
                //ignore
            }
            else if(num_upgradable_cards == 0 && id == "BLESSING_OF_THE_FORGE")//if we have no upgradable cards in hand, then potions that upgrade cards are useless
            {
                //ignore
            }
            else if (another_potion.DynamicVars.ContainsKey("Damage"))//note: damage potions ignore vulnerable and plating
            {
                total_damage_from_potions += another_potion.DynamicVars.Damage.IntValue;
            }
            else
            {
                //NRun.Instance?.GlobalUi.TopBar.PotionContainer.AnimatePotion(another_potion);
                return;//if we get here we have some potion that can still benefit us this turn
            }
        }

        if (total_damage_from_potions > 0 && minimum_enemy_hitpoints <= total_damage_from_potions)//if we can't kill any enemy with our potions, then potions that deals damage are useless
        {
			/*
            foreach (PotionModel another_potion in player.Potions)
            {
				if (another_potion.DynamicVars.ContainsKey("Damage"))
				{
					NPotionContainer? container = NRun.Instance?.GlobalUi.TopBar.PotionContainer;
                    var holders = Traverse.Create(container)
                        .Field<List<NPotionHolder>>("_holders")
                        .Value;

                    var nPotionHolder = holders?.FirstOrDefault(n =>
                        n.Potion != null && n.Potion.Model == another_potion);

					var npotion = nPotionHolder?.Potion;

                    Traverse.Create(npotion).Method("DoFlash").GetValue();
                }
            }
			*/
            return;
        }

        //if it falls here, we have no cards to play at all and no potions which would have sense to use, then end turn automatically
        PlayerCmd.EndTurn(player, true);
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
		else if (cardA.Id.Entry != cardB.Id.Entry) return false;
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
		public static bool IsDiscardTypeOfSelect = false;
	}
}
