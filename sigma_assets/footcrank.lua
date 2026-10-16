-- The foot crank (Engine Sim): an "engine" that is a pedal crank. The model's root is tagged hand_crank (Engine Sim makes
-- it the crank), its crank and fan crank_spin. Built on the 2.8 V6, whose own FSMs still run: the name is changed, and
-- the V6's fan spinner is stilled (its spin is the fan's crank_spin, turning with the crank).
template "foot_crank" {
  fsm = {
    ItemName = { ItemName = "Foot Crank" },
    Rotate = { ["on/SetFloatValue.floatValue"] = 0 },
  },
}
