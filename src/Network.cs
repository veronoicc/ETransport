/*
 * This file is part of ZTransport, copyright ©2020 BloodyRum.
 *
 * ZTransport is free software: you can redistribute it and/or modify it under
 * the terms of the GNU General Public License as published by the Free Soft-
 * ware Foundation, either version 3 of the License, or (at your option) any
 * later version.
 *
 * ZTransport is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FIT-
 * NESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more
 * details.
 *
 * You should have received a copy of the GNU General Public License along with
 * ZTransport. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Net.Sockets;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZTransport
{
    class Point : IEquatable<Point> {
        public int x { get; }
        public int y { get; }

        public Point(int x, int y) {
            this.x = x;
            this.y = y;
        }

        public static Point extract_from_message(JObject message) {
            // ONI's JSON.NET is really ancient... ugh.
            IDictionary<string, JToken> dict
                = (IDictionary<string, JToken>)message;

            if (dict.ContainsKey("x") && dict.ContainsKey("y")) {
                int x_value = (int)message["x"];
                int y_value = (int)message["y"];
                return new Point(x_value, y_value);
            }
            else return null;
        }
        
        public bool Equals(Point other) {
            return this.x == other.x && this.y == other.y;
        }

        // witchcraft
        public override int GetHashCode() {
            int y_hash = y.GetHashCode();
            return x.GetHashCode() ^ (y_hash << 7) ^ (y_hash >> 25);
        }
    }

    class ServerDiedException : Exception {
        public ServerDiedException() {}
        public ServerDiedException(string message)
            : base(message) {}
        public ServerDiedException(string message, Exception inner)
            : base(message, inner) {}
    }
    public class Network {
        NetworkStream stream;
        Thread read_thread, write_thread, ping_thread;

        // access to any of the following variables must be protected by a lock
        // on "this":
        TcpClient client;
        string connection_error;
        BlockingCollection<JObject> outgoing_messages
            = new BlockingCollection<JObject>(new ConcurrentQueue<JObject>());
        Dictionary<Point, JObject> last_request_for_tile
            = new Dictionary<Point, JObject>();
        Dictionary<Point, JObject> last_response_for_tile
            = new Dictionary<Point, JObject>();
        Dictionary<Point, List<string>> registered_remote_devices
            = new Dictionary<Point, List<string>>();
        Dictionary<Point, string> registered_local_devices
            = new Dictionary<Point, string>();
        
        bool connection_active = false;
        bool reconnecting = false;
        // exception to locking rule: outgoing_messages
        // do not need a lock in order to get a message *out of* them
        uint cookie = 0;

        string address;
        ushort port;
        
        public Network() {
            read_thread = new Thread(new ThreadStart(() => read_thread_function())); 
            read_thread.IsBackground = true;
            read_thread.Start();
        }

        public void connect(string address, ushort port) {
            lock(this) {
                if (client != null) {
                    reconnecting = true;
                    client.Close();
                }
                this.address = address;
                this.port = port;
                Monitor.Pulse(this);
            }
        }

        private void write_directly(NetworkStream stream, JObject message) {
            string message_as_string
                = JsonConvert.SerializeObject(message) + "\n";
            Byte[] data
                = System.Text.Encoding.UTF8.GetBytes(message_as_string);
            stream.Write(data, 0, data.Length);
        }

        public void write_thread_function() {
            NetworkStream stream = this.stream;
            while(true) {
                JObject next_message_to_send = outgoing_messages.Take();
                write_directly(stream, next_message_to_send);
            }
        }

        public void ping_thread_function() {
            JObject ping = new JObject();
            ping.Add("type", "ping");
            while(true) {
                int ping_interval = Z.ping_interval;
                if (ping_interval > 0) {
                    Thread.Sleep(ping_interval * 1000);
                    // *1000 because sleep is expecting ms, not s
                    send_message(ping);
                } else {
                    Thread.Sleep(5000);
                }
            }
        }

        public JObject get_message_for(string type, int x, int y) {
            Point requested_tile = new Point(x, y);
            JObject response;
            lock(this) {
                if (last_response_for_tile.TryGetValue(requested_tile,
                                                       out response)
                    && response != null && ((string)response["type"]) == type){
                    last_response_for_tile.Remove(requested_tile);
                    return response;
                } else {
                    return null;
                }
            }
        }

        public void send_message(JObject message) {
            Point target_point = Point.extract_from_message(message);
            bool needs_response = target_point != null && (string)message["type"] != "register" && (string)message["type"] != "unregister";

            lock(this) {
                if (connection_active) {
                    if(needs_response) {
                        // We only need a cookie if:
                        // - The message "belongs to" a particular point
                        //   AND is not a registration message
                        // AND
                        // - The message is going to be sent over the network
                        //   (i.e. the server isn't dead)
                        message["cookie"] = cookie++;
                        // doing it this way instead of using .Add will make it
                        // overwrite any existing request
                        last_request_for_tile[target_point] = message;
                        last_response_for_tile.Remove(target_point);
                    }
                    outgoing_messages.Add(message);
                } else {
                    if (needs_response) {
                        last_response_for_tile[target_point] = generate_missing_response(message);
                    }
                }
            }
        }

        private JObject read_directly(StreamReader sr) {
            string line = sr.ReadLine();
            if(line == null) {
                // FUCK THE WORLD! ALL HELLS GONE LOOSE!
                throw new ServerDiedException("Bye bye");
            }
            return JObject.Parse(line);
        }

        private JObject copy_coords(JObject to_get_from, JObject to_set) {
            to_set.Add("x", to_get_from["x"]);
            to_set.Add("y", to_get_from["y"]);
            return to_set;
        }

        private JObject generate_missing_response(JObject missing_response) {
            JObject fake_returned = new JObject();
            fake_returned = copy_coords(missing_response, fake_returned);

            switch((string)missing_response["type"]) {
                case "send_joules":
                    // Pretend that NO energy was sucsufully sent,
                    // therefor the return amount equals the sent amount

                    fake_returned.Add("type", "sent_joules");
                    fake_returned.Add("spare", missing_response["joules"]);

                    return fake_returned;

                case "recv_joules":
                    // Pretend that NO energy was recieved
                    // therefor the return amount is 0

                    fake_returned.Add("type", "got_joules");
                    fake_returned.Add("joules", 0);

                    return fake_returned;
                case "send_packet":
                    // Pretend that the server rejects the packet

                    fake_returned.Add("type", "sent_packet");
                    fake_returned.Add("phase", missing_response["phase"]);
                    fake_returned.Add("accepted", false);

                    return fake_returned;
                case "recv_packet":
                    // Pretend that we sent back a null packet

                    fake_returned.Add("type", "got_packet");
                    fake_returned.Add("phase", missing_response["phase"]);
                    fake_returned.Add("packet", null);
                    // I assume here that null works to make the value
                    // recieved from the packet to be null

                    return fake_returned;
                default:
                    throw new Exception("GENERATE MISSING: WAS PASSED AN UNKNOWN MESSAGE TYPE");
            }
        }

        private void register_remote_device(Point point, string device_id) {
            lock(this) {
                List<string> devices;
                bool exists = registered_remote_devices.TryGetValue(point, out devices);

                if (!exists) {
                    devices = new List<string>();
                    registered_remote_devices.Add(point, devices);
                }
                devices.Add(device_id);
            }
        }

        private void unregister_remote_device(Point point, string device_id) {
            lock(this) {
                List<string> devices;
                bool exists = registered_remote_devices.TryGetValue(point, out devices);

                if (!exists) {
                    // Attempted to unregister a device at a point where we
                    // don't know about any registered devices.
                    // Since unregistering a device that doesn't exist is not
                    // an error, and a NULL list is being treated as an empty
                    // list, we don't have to do anything. -SB
                    return;
                }

                devices.Remove(device_id);

                if (devices.Count == 0) {
                    registered_remote_devices.Remove(point);
                }
            }
        }

        public bool remote_device_exists(int x, int y, string expected_id) {

            Point point = new Point(x, y);

            lock(this) {
                List<string> devices;
                bool exists = registered_remote_devices.TryGetValue(point, out devices);
                if (!exists) {
                    return false;
                }

                foreach (string device_id in devices) {
                    if (device_id == expected_id) { return true; }
                }

                return false;
            }
        }

        public void register_local_device(int x, int y, string device_id) {
            Point location = new Point(x, y);

            JObject register_message = new JObject();
            register_message.Add("type", "register");
            register_message.Add("x", x);
            register_message.Add("y", y);
            register_message.Add("what", device_id);

            lock(this) {
                registered_local_devices.Remove(location);
                registered_local_devices.Add(location, device_id);
                if (connection_active) {
                    send_message(register_message);
                }
            }
        }

        public void unregister_local_device(int x, int y, string device_id) {
            string existing_device;
            Point location = new Point(x, y);

            JObject unregister_message = new JObject();
            unregister_message.Add("type", "unregister");
            unregister_message.Add("x", x);
            unregister_message.Add("y", y);
            unregister_message.Add("what", device_id);

            lock(this) {
                if (registered_local_devices.TryGetValue(location, out existing_device)) {
                    if (existing_device == device_id) {
                        registered_local_devices.Remove(location);
                    }
                }
                if (connection_active) {
                    send_message(unregister_message);
                }
            }
        }

        private void send_registered_list() {
            lock(this) {
                foreach(KeyValuePair<Point,string> entry in registered_local_devices) {
                    JObject register_message = new JObject();
                    register_message.Add("type", "register");
                    register_message.Add("x", entry.Key.x);
                    register_message.Add("y", entry.Key.y);
                    register_message.Add("what", entry.Value);
                    send_message(register_message);
                }
            }
        }

        private void handle_message(JObject message) {
            switch((string)message["type"]) {
                case "ping":
                    JObject pong = new JObject();
                    pong.Add("type", "pong");
                    send_message(pong);
                    break;
                case "registered":
                    register_remote_device(new Point((int)message["x"],
                                          (int)message["y"]),
                                    (string)message["what"]);
                    break;
                case "unregistered":
                    unregister_remote_device(new Point((int)message["x"],
                                            (int)message["y"]),
                                      (string)message["what"]);
                    break;
                default:
                    lock(this) {
                        Point tile_got = Point.extract_from_message(message);
                        if (tile_got != null) {
                            JObject last_request;
                            last_request_for_tile.TryGetValue(tile_got, out last_request);

                            last_request_for_tile.Remove(tile_got);

                            if(last_request == null) {
                                // We didn't ask for this!
                                Debug.Log("Z-Transport: WARNING: Got a message for a tile when we weren't expecting one for that tile?");
                                break;
                            } else if (!last_request["cookie"]
                                       .Equals(message["cookie"])) {
                                // Cookie doesn't match, this message isn't
                                // a response to the last message sent for
                                // this tile. Discard it.
                                Debug.Log("Z-Transport: WARNING: Got an extra message but filtered it out because the cookie didn't match. (Harmless.)");
                                break;
                            }
                            last_response_for_tile[tile_got] = message;
                        }

                        //  ___
                        // (X-X)
                        //   |
                        // --+--
                        //   |
                        //  / \
                    }
                    break;
            }
        }

        public void read_thread_function() {
            connection_active = false;
            
            while(true) {
                try {
                    lock(this) {
                        connection_error = null;
                        while(address == null) Monitor.Wait(this);
                        client = new TcpClient(address, port);
                        reconnecting = false;
                    }
                }
                catch (SocketException e) {
                    lock(this) {
                        connection_error = e.Message;
                    }
                    Debug.Log("Z-Transport: Unable to connect: " + e.Message);
                    Debug.Log("Z-Transport: Trying again in about 5 seconds.");
                    Thread.Sleep(5000);
                    continue;
                }
                stream = client.GetStream();
                StreamReader sr = new StreamReader(stream);

                JObject hello = new JObject();
                hello.Add("type", "hello");
                hello.Add("proto", "oniz");
                hello.Add("version", 0);
                write_directly(this.stream, hello);

                JObject response = read_directly(sr);
                if ((string)response["type"] == "auth_ok") {
                    // No need to authenticate. We're all fine, here, now.
                } else if ((string)response["type"] == "need_auth") {
                    throw new Exception("NIY: authentication");
                } else {
                    throw new Exception("BAD SERVER AUTH MESSAGE: " + JsonConvert.SerializeObject(response));
                }

                lock(this) {
                    connection_active = true;

                    // make sure we don't have any stale messages hanging
                    // around -SB
                    while(outgoing_messages.Count > 0) {
                        JObject v;
                        outgoing_messages.TryTake(out v);
                    }

                    // When we reconnect, we need to (re)send the
                    // list of registered devices, the server
                    // automatically discards registered devices from
                    // a disconnected client
                    send_registered_list();
                }

                write_thread = new Thread(new ThreadStart(() => write_thread_function()));
                write_thread.IsBackground = true;
                write_thread.Start();

                ping_thread = new Thread(new ThreadStart(() => ping_thread_function()));
                ping_thread.IsBackground = true;
                ping_thread.Start();

                try {
                    while(true) {
                        JObject message = read_directly(sr);

                        handle_message(message);
                    }
                }
                catch(ServerDiedException) {
                    Debug.Log("Z-Transport: Server connection closed.");
                    lock(this) {
                        connection_error = "Server connection closed";
                    }
                }
                catch(IOException) {
                    if (!reconnecting) {
                        Debug.Log("Z-Transport: Server connection interrupted.");
                        lock(this) {
                            connection_error = "Server connection interrupted";
                        }
                    }
                }
                catch(KeyNotFoundException e) {
                    Debug.Log("Z-Transport: Protocol error: "+e);
                    lock(this) {
                        connection_error = "Protocol error";
                    }
                }
                catch(Exception e) {
                    if(e is ThreadAbortException ||
                       (e.InnerException != null
                        && e.InnerException is ThreadAbortException)) {
                        // We've been aborted because the main thread ended.
                        // This is fine.
                        break;
                    } else {
                        // Something else bad happened. This is not fine.
                        throw;
                    }
                }
                // If we got here, it's because the server connection was lost
                // for some reason.
                lock(this) {
                    connection_active = false;
                    foreach(KeyValuePair<Point,JObject> entry in last_request_for_tile) {
                        Point target_point = entry.Key;
                        JObject message = entry.Value;
                        last_response_for_tile[target_point]
                            = generate_missing_response(message);
                    }
                    last_request_for_tile.Clear();
                }
                
                write_thread.Abort();
                ping_thread.Abort();
                // make sure to wait until the threads finishes aborting
                write_thread.Join();
                ping_thread.Join();
                // be tidy
                write_thread = null;
                ping_thread = null;
                client.Close();
                lock (this) {
                    client = null;
                    if(reconnecting) continue;
                }
                Thread.Sleep(5000);
            }
        }
        
        public static JObject make_message(string type, int x, int y) {
            JObject new_message = new JObject();
            new_message.Add("type", type);
            new_message.Add("x", x);
            new_message.Add("y", y);
            return new_message;
        }

        public string get_connection_error() {
            lock (this) {
                return connection_error;
            }
        }
    }
}