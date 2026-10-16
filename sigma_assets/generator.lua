-- The generator set: an engine on a static frame, belted to a DC dynamo that lights the place it stands in.
local parts = require "sigma.parts"
local WC = "NWH.WheelController3D.WheelController"

local g = parts.chassis {
  mass = 250, static = true, drive = "none", parked_brakes = false, add = { "EngineSim.Governor" },
  controller = {
    enginePosition = { -0.062, 0.731, 1.62 }, transmissionPosition = { -0.062, 0.731, 1.62 },
    ["powertrain.clutch.slipTorque"] = 100000,
    ["powertrain.clutch.controlType"] = "UserInput",   -- engaged unless a pedal says otherwise: there is none
    ["powertrain.transmission.gears"] = { -2.216, 0, 3.274, 2.093, 1.439, 1.0, 0.817, 0.651 },
  },
}

-- where it stands, with Engine Sim's experimental electrics on (off, it's an item like any other and the world is left
-- as the game has it): the starter camp (its two tin-can lights become electric lamps), and in place of the game's
-- decorative generators wherever they are
if clr.type("EngineSim.Plugin").Electrics then
  g.places = {
    Starter_Area = {
      at = { 0.5, 4.5, 0 }, turn = 0,
      swap = {
        ["lights_1/lights/tin_can_light_hanging"] = "Camp_13/lights_1/lights/lamp_1",
        ["lights_1/lights/tin_can_light_hanging (1)"] = "Camp_13/lights_1/lights/lamp_1",
      },
    },
  }
  g.replaces = { "9163650183033119127", "2051261853844949522" }
end

-- the engine and what it needs
parts.mount(g, "hinge_engine", "engine", { collider = false })   -- the model has the mount's collider
parts.mount(g, "Radiator Mount", "radiator")
parts.engine_sound(g, { at = "hinge_engine" })
parts.fuel_tank(g, { tank = "Fuel Tank", filler = "Tank Cap", litres = 10, capacity = 25, render = true })
parts.ignition(g, { switch = "Ignition Collider", off = "Ignition Off", on = "Ignition On", start = "Ignition and Starter On",
                    fuel = "Fuel Tank", engine = "hinge_engine" })

-- the controls
parts.lever(g, { mount = "Throttle Mount", handle = "Lever", collider = "Throttle Collider", pivot = "Throttle Pivot",
                 label = "Throttle", target = "root:VehicleController", type = "NWH.VehiclePhysics2.VehicleController",
                 property = "input.Throttle" })
parts.lever(g, { mount = "Belt Clutch Mount", handle = "Belt Clutch", collider = "Belt Clutch Collider", pivot = "Belt Clutch Pivot",
                 label = "Belt Clutch", target = "Belt Drive:EngineSim.CrankBelt", type = "EngineSim.CrankBelt",
                 property = "engagement", more = "Tighten", less = "Slacken" })
g["EngineSim.Governor"] = { on = false, toggle = "Generator", lever = "Throttle Collider", rpm = 2200, maxRpm = 3000, droop = 0.08, lag = 0.3 }

-- the belt: the crank pulley to the generator's pulley (a free wheel), drawn
parts.free_wheel(g, "Generator Axle", { mesh = "Generator Pulley", spin = "Generator Spin", radius = 0.053, width = 0.03, mass = 6,
                                        spring = 0, rolling = 0 })
g.nodes["Belt Drive"] = { parent = "root", add = { "EngineSim.CrankBelt" },
  ["EngineSim.CrankBelt"] = { b = "Generator Axle:" .. WC, radiusA = 0.1376, radiusB = 0.053, capacity = 30, engagement = 0,
                              enginePulley = "Engine Pulley", enginePulleyAxis = { 0, 1, 0 } } }
parts.belt(g, "Belt", { a = "Engine Pulley", b = "Generator Pulley", radius_a = 0.1376, radius_b = 0.053, width = 0.022, sag = 0.03,
                        drive = "Belt Drive:EngineSim.CrankBelt" })

-- the generator: repairable, and its readout
local gen = parts.node(g, "Generator")
gen.collider = { shape = "box", render = true }
gen.add = { "EngineSim.DcGenerator" }
gen["EngineSim.DcGenerator"] = { wheel = "Generator Axle:" .. WC, volts = 24, amps = 100, emf = 6, capacitance = 1 }
parts.repairable(g, "Generator", { suffix = "_generator" })
parts.tooltip(g, "Generator", {
  source = "Generator:EngineSim.DcGenerator",
  name = "Title", line2 = "VoltsText", line3 = "LoadText", use = "Text",
  sources = { use = "root:EngineSim.Governor" },
})

template "generator_set" (g)
