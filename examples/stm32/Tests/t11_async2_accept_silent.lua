rtt.send("async2 2000")
-- negative assertions: the window must stay free of both messages;
-- expect_absent succeeds only on a quiet window, a hit raises with the text
rtt.expect_absent("accepted", 400)
rtt.expect_absent("busy", 100)
rtt.expect("async2 #%d+ done", 3000)
rtt.log("t11 PASS")
