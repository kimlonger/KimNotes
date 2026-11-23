package main

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"flag"
	"fmt"
	"log"
	"net"
	"net/netip"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"
)

const (
	// LiteNetLib 1.3.5 uses property 8 for IPv4 unconnected messages.
	unconnectedHeader byte = 8
	wireVersion            = "KNFT1"
	cookieLifetime         = 30 * time.Second
	roomLifetime           = 3 * time.Minute
	replyInterval          = 300 * time.Millisecond
	expiryInterval         = 2 * time.Second
	maxRooms               = 4096
	maxRequestBytes        = 128
	cookieTagBytes         = 16
	cookieBytes            = 8 + cookieTagBytes
)

type request struct {
	role   byte
	room   string
	cookie []byte
}

type datagram struct {
	target netip.AddrPort
	packet []byte
}

type participant struct {
	address   netip.AddrPort
	lastReply time.Time
}

type pair struct {
	sender, receiver participant
	expires          time.Time
}

// rendezvous is owned by the UDP read loop. Files and transfers have no state here.
type rendezvous struct {
	key       [32]byte
	rooms     map[string]*pair
	nextSweep time.Time
}

func parseRequest(packet []byte) (request, bool) {
	if len(packet) < 2 || len(packet) > maxRequestBytes || packet[0] != unconnectedHeader {
		return request{}, false
	}
	fields := strings.Split(string(packet[1:]), " ")
	if len(fields) != 4 || fields[0] != wireVersion || (fields[1] != "r" && fields[1] != "s") {
		return request{}, false
	}
	room := fields[2]
	if len(room) != 64 || strings.ContainsFunc(room, func(c rune) bool {
		return !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')
	}) {
		return request{}, false
	}
	var cookie []byte
	if fields[3] != "-" {
		if len(fields[3]) != base64.RawURLEncoding.EncodedLen(cookieBytes) {
			return request{}, false
		}
		var err error
		cookie, err = base64.RawURLEncoding.Strict().DecodeString(fields[3])
		if err != nil || len(cookie) != cookieBytes {
			return request{}, false
		}
	}
	return request{role: fields[1][0], room: room, cookie: cookie}, true
}

func (s *rendezvous) tag(timestamp []byte, r request, from netip.AddrPort) []byte {
	address := from.Addr().As4()
	port := binary.BigEndian.AppendUint16(nil, from.Port())
	mac := hmac.New(sha256.New, s.key[:])
	mac.Write([]byte(wireVersion))
	mac.Write(timestamp)
	mac.Write(address[:])
	mac.Write(port)
	mac.Write([]byte{r.role})
	mac.Write([]byte(r.room))
	return mac.Sum(nil)[:cookieTagBytes]
}

func (s *rendezvous) issueCookie(now time.Time, r request, from netip.AddrPort) string {
	stamp := binary.BigEndian.AppendUint64(nil, uint64(now.Unix()))
	return base64.RawURLEncoding.EncodeToString(append(stamp, s.tag(stamp, r, from)...))
}

func (s *rendezvous) validCookie(now time.Time, r request, from netip.AddrPort) bool {
	if r.cookie == nil {
		return false
	}
	value := r.cookie
	issuedAt := time.Unix(int64(binary.BigEndian.Uint64(value[:8])), 0)
	age := now.Sub(issuedAt)
	return age >= 0 && age < cookieLifetime && hmac.Equal(value[8:], s.tag(value[:8], r, from))
}

func reply(target netip.AddrPort, parts ...string) datagram {
	return datagram{target: target, packet: append([]byte{unconnectedHeader}, strings.Join(parts, " ")...)}
}

func notifyPeer(now time.Time, target, peer *participant, room string, replies []datagram) []datagram {
	if now.Sub(target.lastReply) < replyInterval {
		return replies
	}
	target.lastReply = now
	return append(replies, reply(target.address, wireVersion, "peer", room, peer.address.Addr().String(), fmt.Sprint(peer.address.Port())))
}

func (s *rendezvous) sweep(now time.Time) {
	if now.Before(s.nextSweep) {
		return
	}
	for id, state := range s.rooms {
		if !now.Before(state.expires) {
			delete(s.rooms, id)
		}
	}
	s.nextSweep = now.Add(expiryInterval)
}

func (s *rendezvous) handle(now time.Time, from netip.AddrPort, packet []byte) []datagram {
	r, ok := parseRequest(packet)
	if !ok || !from.Addr().Is4() || from.Addr().IsUnspecified() || from.Addr().IsMulticast() || from.Port() == 0 {
		return nil
	}
	if !s.validCookie(now, r, from) {
		return []datagram{reply(from, wireVersion, "challenge", s.issueCookie(now, r, from))}
	}

	s.sweep(now)
	state := s.rooms[r.room]
	if state == nil || !now.Before(state.expires) {
		if state == nil && len(s.rooms) >= maxRooms {
			return nil
		}
		state = &pair{expires: now.Add(roomLifetime)}
		s.rooms[r.room] = state
	}
	local, remote := &state.sender, &state.receiver
	if r.role == 'r' {
		local, remote = remote, local
	}
	if local.address.IsValid() && local.address != from || remote.address == from {
		return nil
	}
	newPeer := !local.address.IsValid()
	local.address = from
	if !remote.address.IsValid() {
		return nil
	}

	replies := notifyPeer(now, local, remote, r.room, nil)
	if newPeer {
		replies = notifyPeer(now, remote, local, r.room, replies)
	}
	return replies
}

func serve(conn *net.UDPConn, state *rendezvous) error {
	// Read into a full UDP buffer so oversize packets can be discarded consistently.
	packet := make([]byte, 65535)
	for {
		n, from, err := conn.ReadFromUDPAddrPort(packet)
		if err != nil {
			if errors.Is(err, net.ErrClosed) {
				return nil
			}
			return err
		}
		for _, message := range state.handle(time.Now(), from, packet[:n]) {
			if _, err := conn.WriteToUDPAddrPort(message.packet, message.target); errors.Is(err, net.ErrClosed) {
				return nil
			}
		}
	}
}

func main() {
	listen := flag.String("listen", "0.0.0.0:43763", "IPv4 UDP address for KimNotes device pairing")
	flag.Parse()
	address, err := net.ResolveUDPAddr("udp4", *listen)
	if err != nil {
		log.Fatal(err)
	}
	conn, err := net.ListenUDP("udp4", address)
	if err != nil {
		log.Fatal(err)
	}
	defer conn.Close()
	state := &rendezvous{rooms: make(map[string]*pair)}
	if _, err := rand.Read(state.key[:]); err != nil {
		log.Fatal(err)
	}
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)
	defer signal.Stop(stop)
	go func() {
		<-stop
		conn.Close()
	}()
	log.Printf("KimNotes file-transfer rendezvous listening on %s", conn.LocalAddr())
	if err := serve(conn, state); err != nil {
		log.Fatal(err)
	}
}
