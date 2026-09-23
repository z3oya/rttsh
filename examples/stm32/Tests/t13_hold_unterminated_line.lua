local delay = 1500
rtt.send("async1 "..delay)
local r = rtt.expect("async1 #%d+ accepted", 500)
local id = r:match("#(%d+)")
rtt.send_hex("7a 7a")   -- "zz" with no CR: line stays unsubmitted
-- window must outlast the armed delay, else the negative proof is vacuous
local held, herr = pcall(function() rtt.expect("async1 #"..id.." done", delay + 300) end)
assert(not held and tostring(herr):find("not found", 1, true),
  "FAIL: expected done to stay held, got: "..tostring(herr))
rtt.send_hex("0d")      -- CR submits the held line
rtt.expect("not found", 500)
rtt.expect("async1 #"..id.." done", 1000)
rtt.log("t13 PASS (id="..id..")")
