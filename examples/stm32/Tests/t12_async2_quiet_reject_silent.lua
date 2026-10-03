rtt.send("async2 2500")
rtt.send("async2 quiet")   -- back to back on purpose: must reject silently
rtt.expect_absent("busy", 400)
rtt.send("tick")
rtt.expect("tick: off", 500)   -- probe: console alive
rtt.expect("async2 #%d+ done", 3300)
rtt.log("t12 PASS")
