-- t0 is taken before the send: the firmware starts counting at parse time,
-- so timing from the accept reply under-reads by one round-trip.
local function timed_done(ms)
  local t0 = rtt.now()
  rtt.send("async1 "..ms)
  rtt.expect("async1 #%d+ accepted, due in "..ms.." ms", 500)
  rtt.expect("async1 #%d+ done", ms + 1500)
  local elapsed = rtt.now() - t0
  assert(elapsed >= ms - 20,
    string.format("done too early: %.0f ms (want >= %d)", elapsed, ms))
  assert(elapsed <= ms + 1000,
    string.format("done too late: %.0f ms (want <= %d)", elapsed, ms))
  return elapsed
end

local results = {}
for _, ms in ipairs({100, 300, 700}) do
  results[#results+1] = string.format("ms=%d elapsed=%.0fms", ms, timed_done(ms))
end
rtt.log("t14 PASS: "..table.concat(results, ", "))
