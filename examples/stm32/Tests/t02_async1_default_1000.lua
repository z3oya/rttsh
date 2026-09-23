rtt.send("async1")
rtt.expect("async1 #%d+ accepted, due in 1000 ms", 500)
rtt.expect("async1 #%d+ done", 2500)
rtt.log("t02 PASS")
