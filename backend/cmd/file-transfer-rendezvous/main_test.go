package main

import (
	"bytes"
	"fmt"
	"net"
	"net/netip"
	"strings"
	"testing"
	"time"
)

const room = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

func requestPacket(role, room, cookie string, candidates ...string) []byte {
	parts := append([]string{wireVersion, role, room, cookie}, candidates...)
	return append([]byte{unconnectedHeader}, strings.Join(parts, " ")...)
}

func challengeToken(t *testing.T, message []byte) string {
	t.Helper()
	prefix := append([]byte{unconnectedHeader}, wireVersion+" challenge "...)
	if !bytes.HasPrefix(message, prefix) || len(message) <= len(prefix) {
		t.Fatalf("expected challenge, got %q", message)
	}
	return string(message[len(prefix):])
}

func TestRendezvousOnlyRegistersValidatedEndpoints(t *testing.T) {
	state := &rendezvous{rooms: make(map[string]*pair)}
	now := time.Unix(1800000000, 0)
	receiver := netip.MustParseAddrPort("192.0.2.18:51230")
	sender := netip.MustParseAddrPort("198.51.100.27:52134")
	other := netip.MustParseAddrPort("203.0.113.12:51230")
	initial := requestPacket("r", room, "-")

	got := state.handle(now, receiver, initial)
	if len(got) != 1 || got[0].target != receiver || len(got[0].packet) > len(initial) || len(state.rooms) != 0 {
		t.Fatalf("initial request altered state or returned an invalid challenge: %#v", got)
	}
	token := challengeToken(t, got[0].packet)
	for _, attempt := range []struct {
		address netip.AddrPort
		role    string
		room    string
		when    time.Time
	}{
		{other, "r", room, now},
		{netip.MustParseAddrPort("192.0.2.18:51231"), "r", room, now},
		{receiver, "s", room, now},
		{receiver, "r", strings.Repeat("a", 64), now},
		{receiver, "r", room, now.Add(cookieLifetime)},
	} {
		got = state.handle(attempt.when, attempt.address, requestPacket(attempt.role, attempt.room, token))
		if len(got) != 1 || len(state.rooms) != 0 {
			t.Fatalf("unverified endpoint registered: %#v", attempt)
		}
		challengeToken(t, got[0].packet)
	}

	registered := requestPacket("r", room, token)
	if got = state.handle(now, receiver, registered); len(got) != 0 {
		t.Fatalf("unpaired endpoint received peer data: %#v", got)
	}
	otherChallenge := state.handle(now, other, initial)
	if got = state.handle(now, other, requestPacket("r", room, challengeToken(t, otherChallenge[0].packet))); len(got) != 0 {
		t.Fatalf("second receiver stole the occupied slot: %#v", got)
	}

	senderChallenge := state.handle(now, sender, requestPacket("s", room, "-"))
	senderPacket := requestPacket("s", room, challengeToken(t, senderChallenge[0].packet))
	got = state.handle(now, sender, senderPacket)
	if len(got) != 2 || got[0].target != sender || got[1].target != receiver {
		t.Fatalf("expected initial pairing replies to both endpoints, got %#v", got)
	}
	assertIntroduced(t, got[0].packet, receiver)
	assertIntroduced(t, got[1].packet, sender)

	if got = state.handle(now.Add(replyInterval/2), sender, senderPacket); len(got) != 0 {
		t.Fatalf("immediate replay returned unexpected replies: %#v", got)
	}
	if got = state.handle(now.Add(replyInterval), sender, senderPacket); len(got) != 1 || got[0].target != sender {
		t.Fatalf("refresh should return a single peer address: %#v", got)
	}
}

func TestCapacityAndExpiration(t *testing.T) {
	state := &rendezvous{rooms: make(map[string]*pair)}
	now := time.Unix(1800000000, 0)
	source := netip.MustParseAddrPort("192.0.2.10:43765")
	for i := 0; i < maxRooms; i++ {
		state.rooms[fmt.Sprintf("%064x", i)] = &pair{expires: now.Add(roomLifetime)}
	}
	token := state.issueCookie(now, request{role: 'r', room: room}, source)
	state.handle(now, source, requestPacket("r", room, token))
	if len(state.rooms) != maxRooms || state.rooms[room] != nil {
		t.Fatalf("active room limit exceeded: %d", len(state.rooms))
	}
	expired := now.Add(roomLifetime)
	token = state.issueCookie(expired, request{role: 'r', room: room}, source)
	state.handle(expired, source, requestPacket("r", room, token))
	if len(state.rooms) != 1 || state.rooms[room] == nil {
		t.Fatalf("expired rooms did not release their capacity: %d", len(state.rooms))
	}
}

func TestRendezvousAnnouncesCandidatesOnlyToOptedInPeers(t *testing.T) {
	const receiverCandidates = "10.1.0.3:51230,172.16.8.4:51230,192.168.0.3:51230,[2001:db8:1::3]:51230"
	const senderCandidates = "[2001:db8:2::27]:52134,10.2.0.27:52134"
	for _, tc := range []struct {
		name                         string
		receiverFields, senderFields []string
		wantReceiver, wantSender     []string
	}{
		{"legacy", nil, nil, nil, nil},
		{"legacy receiver", nil, []string{senderCandidates}, nil, nil},
		{"legacy sender", []string{receiverCandidates}, nil, nil, nil},
		{"both opted in", []string{receiverCandidates}, []string{senderCandidates}, []string{senderCandidates}, []string{receiverCandidates}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			state := &rendezvous{rooms: make(map[string]*pair)}
			now := time.Unix(1800000000, 0)
			receiver := netip.MustParseAddrPort("192.0.2.18:51230")
			sender := netip.MustParseAddrPort("198.51.100.27:52134")
			register := func(address netip.AddrPort, role string, fields []string) []datagram {
				t.Helper()
				legacyChallenge := state.handle(now, address, requestPacket(role, room, "-"))
				challenge := state.handle(now, address, requestPacket(role, room, "-", fields...))
				if len(legacyChallenge) != 1 || len(challenge) != 1 || !bytes.Equal(challenge[0].packet, legacyChallenge[0].packet) {
					t.Fatalf("opt-in changed the initial challenge: %#v", challenge)
				}
				cookie := challengeToken(t, challenge[0].packet)
				return state.handle(now, address, requestPacket(role, room, cookie, fields...))
			}
			if got := register(receiver, "r", tc.receiverFields); len(got) != 0 {
				t.Fatalf("unmatched receiver received data: %#v", got)
			}
			introductions := register(sender, "s", tc.senderFields)
			if len(introductions) != 2 || introductions[0].target != sender || introductions[1].target != receiver {
				t.Fatalf("wrong introduction destinations: %#v", introductions)
			}
			assertIntroduced(t, introductions[0].packet, receiver, tc.wantSender...)
			assertIntroduced(t, introductions[1].packet, sender, tc.wantReceiver...)
			now = now.Add(replyInterval)
			fallback := register(receiver, "r", nil)
			if len(fallback) != 1 || fallback[0].target != receiver {
				t.Fatalf("legacy retry did not receive an introduction: %#v", fallback)
			}
			assertIntroduced(t, fallback[0].packet, sender, tc.wantReceiver...)
		})
	}
}

func TestCandidateFieldsMustBeCanonicalAndBounded(t *testing.T) {
	receiver := netip.MustParseAddrPort("192.0.2.18:51230")
	now := time.Unix(1800000000, 0)
	for _, field := range []string{
		"", "-", "8.8.8.8:53", "127.0.0.1:51230", "100.64.0.1:1", "192.168.0.3:0",
		"192.168.0.3:051230", "[2001:DB8::1]:51230", "[2001:db8:0:0::1]:51230",
		"[2001:db8::1%ethernet]:51230", "[::ffff:192.168.0.3]:51230", "[fe80::1]:51230",
		"[fc00::1]:51230", "192.168.0.3:51230,", "192.168.0.3:51230,8.8.8.8:53",
		strings.Repeat("192.168.0.3:51230,", 4) + "192.168.0.3:51230",
		strings.Repeat("6", maxCandidatesBytes+1),
	} {
		t.Run(field, func(t *testing.T) {
			state := &rendezvous{rooms: make(map[string]*pair)}
			if got := state.handle(now, receiver, requestPacket("r", room, "-", field)); len(got) != 0 || len(state.rooms) != 0 {
				t.Fatalf("invalid candidate field gave a reply or created a room: %q %#v", field, got)
			}
		})
	}
	maximum := "[2fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff]:65535"
	candidates := strings.Repeat(maximum+",", maxCandidates-1) + maximum
	if len(candidates) != maxCandidatesBytes {
		t.Fatalf("maximum candidate test list is %d bytes", len(candidates))
	}
	packet := requestPacket("r", room, strings.Repeat("A", 32), candidates)
	if _, ok := parseRequest(packet); !ok {
		t.Fatal("four maximum-length candidates were rejected")
	}
}

func assertIntroduced(t *testing.T, packet []byte, peer netip.AddrPort, candidates ...string) {
	t.Helper()
	parts := append([]string{wireVersion, "peer", room, peer.Addr().String(), fmt.Sprint(peer.Port())}, candidates...)
	want := reply(netip.AddrPort{}, parts...).packet
	if !bytes.Equal(packet, want) {
		t.Fatalf("bad introduction: got %q, want %q", packet, want)
	}
}

func TestUDPLoopbackPairingAndPayloadRejection(t *testing.T) {
	server, err := net.ListenUDP("udp4", net.UDPAddrFromAddrPort(netip.MustParseAddrPort("127.0.0.1:0")))
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- serve(server, &rendezvous{rooms: make(map[string]*pair)}) }()
	t.Cleanup(func() {
		server.Close()
		if err := <-done; err != nil {
			t.Errorf("UDP service: %v", err)
		}
	})

	connect := func() *net.UDPConn {
		t.Helper()
		client, err := net.DialUDP("udp4", nil, server.LocalAddr().(*net.UDPAddr))
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(func() { client.Close() })
		return client
	}
	receiver, sender := connect(), connect()
	register := func(conn *net.UDPConn, role string, initial []byte) {
		t.Helper()
		if _, err := conn.Write(initial); err != nil {
			t.Fatal(err)
		}
		token := challengeToken(t, readReply(t, conn))
		if _, err := conn.Write(requestPacket(role, room, token)); err != nil {
			t.Fatal(err)
		}
	}
	register(receiver, "r", requestPacket("r", room, "-"))
	register(sender, "s", requestPacket("s", room, "-"))
	assertIntroduced(t, readReply(t, receiver), sender.LocalAddr().(*net.UDPAddr).AddrPort())
	assertIntroduced(t, readReply(t, sender), receiver.LocalAddr().(*net.UDPAddr).AddrPort())

	for _, malformed := range [][]byte{
		append([]byte{unconnectedHeader}, []byte("arbitrary file contents")...),
		append(requestPacket("s", room, "-"), bytes.Repeat([]byte{'a'}, maxRequestBytes)...),
		requestPacket("s", strings.ToUpper(room), "-"),
		requestPacket("s", room, "invalid"),
	} {
		if _, err := sender.Write(malformed); err != nil {
			t.Fatal(err)
		}
	}
	for _, client := range []*net.UDPConn{receiver, sender} {
		if err := client.SetReadDeadline(time.Now().Add(80 * time.Millisecond)); err != nil {
			t.Fatal(err)
		}
		var buffer [maxRequestBytes]byte
		if n, err := client.Read(buffer[:]); err == nil {
			t.Fatalf("received unexpected response or forwarded payload: %q", buffer[:n])
		}
	}
}

func readReply(t *testing.T, conn *net.UDPConn) []byte {
	t.Helper()
	if err := conn.SetReadDeadline(time.Now().Add(2 * time.Second)); err != nil {
		t.Fatal(err)
	}
	var buffer [maxRequestBytes]byte
	n, err := conn.Read(buffer[:])
	if err != nil {
		t.Fatal(err)
	}
	return buffer[:n]
}
