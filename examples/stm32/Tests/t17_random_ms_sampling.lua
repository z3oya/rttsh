-- t0 is taken before the send: the firmware starts counting at parse time.
-- Seed is second-resolution, so repeats within the same second draw the same samples.
math.randomseed(os.time())
local results = {}
for i = 1, 5 do
  local ms = math.random(80, 400)
  local t0 = rtt.now()
  rtt.send("async1 "..ms)
  rtt.expect("async1 #%d+ accepted, due in "..ms.." ms", 500)
  rtt.expect("async1 #%d+ done", 2500)
  local elapsed = rtt.now() - t0
  assert(elapsed >= ms - 20 and elapsed <= ms + 1000,
    string.format("sample %d: ms=%d elapsed=%.0f out of window", i, ms, elapsed))
  results[#results+1] = string.format("ms=%d/%.0fms", ms, elapsed)
end
rtt.log("t17 PASS: "..table.concat(results, ", "))
