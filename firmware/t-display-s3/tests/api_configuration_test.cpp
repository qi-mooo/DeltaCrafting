#include <cassert>
#include <string>
#include "api_configuration.h"

int main()
{
    DynamicJsonDocument saved(2048), request(2048);
    saved["wifiSsid"] = "existing-wifi"; saved["wifiPassword"] = "existing-secret";
    saved["baseUrl"] = "http://10.0.0.183:17890"; saved["apiKey"] = "old-key";
    saved["otherSetting"] = 123;
    request["baseUrl"] = "http://10.0.0.181:17890";
    request["apiKey"] = "0123456789abcdef0123456789abcdef";
    assert(!ApiConfiguration::apply(request.as<JsonObjectConst>(), saved.as<JsonObject>()));
    assert(saved["baseUrl"] == "http://10.0.0.181:17890");
    assert(saved["wifiPassword"] == "existing-secret");
    assert(saved["wifiSsid"] == "existing-wifi");
    assert(saved["otherSetting"] == 123);
    std::string before; serializeJson(saved, before);
    request["wifiSsid"] = "replacement";
    assert(ApiConfiguration::apply(request.as<JsonObjectConst>(), saved.as<JsonObject>()));
    std::string after; serializeJson(saved, after); assert(before == after);
    request["wifiPassword"] = "";
    assert(!ApiConfiguration::apply(request.as<JsonObjectConst>(), saved.as<JsonObject>()));
    assert(saved["wifiSsid"] == "replacement" && saved["wifiPassword"] == "");
    request["baseUrl"] = "http://10.0.0.181:17890/api/v1/status";
    assert(ApiConfiguration::apply(request.as<JsonObjectConst>(), saved.as<JsonObject>()));
    request["baseUrl"] = "http://10.0.0.181:80";
    assert(ApiConfiguration::apply(request.as<JsonObjectConst>(), saved.as<JsonObject>()));
    request["baseUrl"] = "http://10.0.0.181:70000";
    assert(ApiConfiguration::apply(request.as<JsonObjectConst>(), saved.as<JsonObject>()));
    request["baseUrl"] = "http://127.0.0.1:17890";
    assert(ApiConfiguration::apply(request.as<JsonObjectConst>(), saved.as<JsonObject>()));
    request["baseUrl"] = "http://example.local:17890";
    assert(ApiConfiguration::apply(request.as<JsonObjectConst>(), saved.as<JsonObject>()));
    request["baseUrl"] = "http://10.0.0.181:17890";
    request.remove("wifiSsid"); request.remove("wifiPassword");
    saved.clear(); saved.to<JsonObject>();
    assert(ApiConfiguration::apply(request.as<JsonObjectConst>(), saved.as<JsonObject>()));
    assert(saved.size() == 0);
}
