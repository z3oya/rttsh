-- Single source of the rtt shim: LuaScriptHost embeds this file as an
-- assembly resource; the Rust tests include it via include_str!.
local function protect(fn)
    return function(...)
        local r = table.pack(pcall(fn, ...))
        if not r[1] then error(r[2], 2) end
        return table.unpack(r, 2, r.n)
    end
end
local function floor_arg(v, message)
    local n = tonumber(v)
    if n == nil then error(message, 0) end
    return math.floor(n)
end
local default_timeout = 1000
rtt = {
    send = protect(function(text)
        local st, msg = HOST_send(text)
        if st ~= 0 then error(msg, 0) end
    end),
    send_hex = protect(function(hex)
        local st, msg = HOST_send_hex(hex)
        if st ~= 0 then error(msg, 0) end
    end),
    log = protect(function(line)
        local st, msg = HOST_log(tostring(line))
        if st ~= 0 then error(msg, 0) end
    end),
    wait = protect(function(ms)
        if ms == nil then error('wait: missing timeout in ms', 0) end
        local st, res = HOST_wait(math.floor(ms))
        if st ~= 0 then error(res, 0) end
        return res
    end),
    wait_hex = protect(function(ms)
        if ms == nil then error('wait_hex: missing timeout in ms', 0) end
        local st, res = HOST_wait_hex(math.floor(ms))
        if st ~= 0 then error(res, 0) end
        return res
    end),
    expect = protect(function(pattern, timeoutMs)
        if pattern == nil then error('expect: missing pattern', 0) end
        if type(pattern) ~= 'string' then error('expect: pattern must be a string', 0) end
        local st, res = HOST_expect(pattern, math.floor(timeoutMs or default_timeout), 0)
        if st ~= 0 then error(res, 0) end
        -- find, not match: match returns captures only and drops the whole match
        local f = table.pack(res:find(pattern))
        return res, res:sub(f[1], f[2]), table.unpack(f, 3, f.n)
    end),
    now = protect(function() return HOST_now() end),
    sleep = protect(function(ms)
        local st, msg = HOST_sleep(math.floor(tonumber(ms) or 0))
        if st ~= 0 then error(msg, 0) end
    end),
    exit = function(code)
        local n = math.floor(tonumber(code) or 0)
        local st, msg = HOST_exit(n)
        if st ~= 0 then error(msg, 0) end
        error('__rtt_exit=' .. n, 0)
    end,
    set_timeout = protect(function(ms)
        if ms == nil then error('set_timeout: missing timeout in ms', 0) end
        default_timeout = floor_arg(ms, 'set_timeout: timeout must be a number')
    end),
    mem_read = protect(function(addr, count, width)
        if addr == nil then error('mem_read: missing address', 0) end
        local w = width == nil and 32 or floor_arg(width, 'mem_read: width must be a number')
        local n = count == nil and 1 or floor_arg(count, 'mem_read: count must be a number')
        local st, res = HOST_mem_read(floor_arg(addr, 'mem_read: address must be a number'), n, w)
        if st ~= 0 then error(res, 0) end
        if count == nil then return res[1] end
        return res
    end),
    mem_write = protect(function(addr, values, width)
        if addr == nil then error('mem_write: missing address', 0) end
        if values == nil then error('mem_write: missing value(s)', 0) end
        local a = floor_arg(addr, 'mem_write: address must be a number')
        local w = width == nil and 32 or floor_arg(width, 'mem_write: width must be a number')
        if type(values) == 'table' then
            local n = #values
            if n == 0 then return end
            local copy = {}
            for i = 1, n do
                local v = tonumber(values[i])
                if v == nil then error('mem_write: value #' .. i .. ' is not a number', 0) end
                copy[i] = math.floor(v)
            end
            local st, msg = HOST_mem_write_table(a, copy, n, w)
            if st ~= 0 then error(msg, 0) end
            return
        end
        local st, msg = HOST_mem_write_one(a, floor_arg(values, 'mem_write: value must be a number or table'), w)
        if st ~= 0 then error(msg, 0) end
    end),
    is_halted = protect(function()
        local st, v = HOST_is_halted()
        if st ~= 0 then error(v, 0) end
        return v
    end),
    halt = protect(function()
        local st, msg = HOST_halt()
        if st ~= 0 then error(msg, 0) end
    end),
    resume = protect(function()
        local st, msg = HOST_resume()
        if st ~= 0 then error(msg, 0) end
    end),
    flush = protect(function()
        local st, msg = HOST_flush()
        if st ~= 0 then error(msg, 0) end
    end),
    try_expect = protect(function(pattern, timeoutMs)
        if pattern == nil then error('try_expect: missing pattern', 0) end
        if type(pattern) ~= 'string' then error('try_expect: pattern must be a string', 0) end
        local st, res = HOST_expect(pattern, math.floor(timeoutMs or default_timeout), 1)
        if st == 2 then return nil, res end
        if st ~= 0 then error(res, 0) end
        local f = table.pack(res:find(pattern))
        return res, res:sub(f[1], f[2]), table.unpack(f, 3, f.n)
    end),
    expect_absent = protect(function(pattern, timeoutMs)
        if pattern == nil then error('expect_absent: missing pattern', 0) end
        if type(pattern) ~= 'string' then error('expect_absent: pattern must be a string', 0) end
        local st, msg = HOST_expect_absent(pattern, math.floor(timeoutMs or default_timeout))
        if st ~= 0 then error(msg, 0) end
    end),
    expect_any = protect(function(ms, ...)
        if ms == nil then error('expect_any: missing timeout in ms', 0) end
        local n = select('#', ...)
        if n == 0 then error('expect_any: missing patterns', 0) end
        local patterns = {}
        for i = 1, n do
            local p = select(i, ...)
            if type(p) ~= 'string' then error('expect_any: pattern #' .. i .. ' must be a string', 0) end
            if p == '' then error('expect_any: pattern #' .. i .. ' is empty', 0) end
            patterns[i] = p
        end
        local st, res, idx = HOST_expect_any(patterns, math.floor(ms))
        if st ~= 0 then error(res, 0) end
        local f = table.pack(res:find(patterns[idx + 1]))
        return idx + 1, res, res:sub(f[1], f[2]), table.unpack(f, 3, f.n)
    end),
    -- timeout is nil (never ""), an empty line is ""
    read_line = protect(function(ms)
        local st, res = HOST_expect('\n', math.floor(ms or default_timeout), 1)
        if st == 2 then return nil end
        if st ~= 0 then error(res, 0) end
        return (res:gsub('\r?\n$', ''))
    end),
}
