import Foundation

/// The ONLY component that talks off-device (design #276 boundary): a thin async wrapper over the
/// Home PC Server's §5 endpoints the canvas needs. Never calls an AI provider or Unity directly.
public actor ServerClient {
    private let baseURL: URL
    private let session: URLSession
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()

    public init(baseURL: URL, session: URLSession = .shared) {
        self.baseURL = baseURL
        self.session = session
    }

    public enum ServerError: Error, Equatable {
        case http(Int)          // non-2xx status
        case emptyBody
    }

    // POST /canvas — upsert the layered canvas document; returns the stored form.
    public func saveCanvas(_ canvas: FacadeCanvas) async throws -> FacadeCanvas {
        try await post("canvas", body: canvas)
    }

    // GET /canvas/{osm_id}/{facade} — load a facade's canvas, or nil on 404.
    public func loadCanvas(osmId: Int, facade: String) async throws -> FacadeCanvas? {
        // appendingPathComponent percent-encodes as needed; don't double-encode.
        let path = "canvas/\(osmId)/\(facade)"
        do {
            return try await get(path) as FacadeCanvas
        } catch ServerError.http(404) {
            return nil
        }
    }

    // GET /canvas/{osm_id} — every facade canvas authored for a building.
    public func listCanvases(osmId: Int) async throws -> [FacadeCanvas] {
        try await get("canvas/\(osmId)")
    }

    // POST /ai/signs/generate — server-mediated; returns a reusable sign asset record.
    public func generateSign(_ request: SignRequest) async throws -> SignDef {
        try await post("ai/signs/generate", body: request)
    }

    // PUT /canvas/{osm_id}/{facade}/backdrop — upload a photo as the facade reference backdrop.
    public func uploadBackdrop(osmId: Int, facade: String, data imageData: Data) async throws {
        let boundary = "Boundary-FacadeCanvas"
        var req = URLRequest(url: baseURL.appendingPathComponent("canvas/\(osmId)/\(facade)/backdrop"))
        req.httpMethod = "PUT"
        req.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        var body = Data()
        func s(_ str: String) { body.append(contentsOf: str.utf8) }
        s("--\(boundary)\r\n")
        s("Content-Disposition: form-data; name=\"file\"; filename=\"backdrop.jpg\"\r\n")
        s("Content-Type: image/jpeg\r\n\r\n")
        body.append(imageData)
        s("\r\n--\(boundary)--\r\n")
        req.httpBody = body
        let (_, response) = try await session.data(for: req)
        guard let http = response as? HTTPURLResponse else { throw ServerError.emptyBody }
        guard (200..<300).contains(http.statusCode) else { throw ServerError.http(http.statusCode) }
    }

    // GET /canvas/{osm_id}/{facade}/backdrop — reference render for drawing over; nil on 404.
    // The G2 asset-binary endpoint (#300) will serve the real UV-mapped render once shipped;
    // this returns nil gracefully until then.
    public func fetchBackdrop(osmId: Int, facade: String) async throws -> Data? {
        var req = URLRequest(url: baseURL.appendingPathComponent("canvas/\(osmId)/\(facade)/backdrop"))
        req.httpMethod = "GET"
        let (data, response) = try await session.data(for: req)
        guard let http = response as? HTTPURLResponse else { return nil }
        if http.statusCode == 404 { return nil }
        guard (200..<300).contains(http.statusCode) else { throw ServerError.http(http.statusCode) }
        return data
    }

    // POST /palettes — create or replace a named facade palette.
    public func createPalette(_ palette: Palette) async throws -> Palette {
        try await post("palettes", body: palette)
    }

    // POST /export/unity — materialise Assets/SFBuildingTemplates/ on the server (design D4).
    // outDir defaults to the server's env-configured export directory; scope defaults to .city
    // (today's original unscoped behavior), so existing callers are unaffected (#346).
    public func publishToUnity(outDir: String = "", scope: ExportScope = .city,
                                osmId: Int? = nil, neighborhood: String = "") async throws -> ExportResult {
        try await post("export/unity", body: ExportRequest(outDir: outDir, scope: scope,
                                                             osm_id: osmId, neighborhood: neighborhood))
    }

    // GET /buildings — paginated list, optionally filtered by neighborhood and type.
    public func listBuildings(neighborhood: String? = nil, type: String? = nil,
                               limit: Int = 50, offset: Int = 0) async throws -> BuildingPage {
        guard var components = URLComponents(url: baseURL.appendingPathComponent("buildings"),
                                              resolvingAgainstBaseURL: false) else {
            throw ServerError.emptyBody
        }
        var items: [URLQueryItem] = [
            URLQueryItem(name: "limit", value: "\(limit)"),
            URLQueryItem(name: "offset", value: "\(offset)"),
        ]
        if let n = neighborhood { items.append(URLQueryItem(name: "neighborhood", value: n)) }
        if let t = type         { items.append(URLQueryItem(name: "type", value: t)) }
        components.queryItems = items
        guard let url = components.url else { throw ServerError.emptyBody }
        var req = URLRequest(url: url)
        req.httpMethod = "GET"
        return try await send(req)
    }

    // GET /buildings/{osm_id} — full facts for one building; nil on 404.
    public func getBuilding(osmId: Int) async throws -> BuildingFacts? {
        do {
            return try await get("buildings/\(osmId)") as BuildingFacts
        } catch ServerError.http(404) {
            return nil
        }
    }

    // GET /templates — list all authored templates.
    public func listTemplates() async throws -> [TemplateDef] {
        try await get("templates")
    }

    // POST /templates — create or upsert a template.
    public func createTemplate(_ template: TemplateDef) async throws -> TemplateDef {
        try await post("templates", body: template)
    }

    // GET /parts — list all authored part records.
    public func listParts() async throws -> [PartDef] {
        try await get("parts")
    }

    // GET /districts — list all authored districts (#341).
    public func listDistricts() async throws -> [DistrictDef] {
        try await get("districts")
    }

    // POST /parts — create or upsert a part record.
    public func createPart(_ part: PartDef) async throws -> PartDef {
        try await post("parts", body: part)
    }

    // GET /buildings/{osm_id}/thumb — rendered 3D preview image; nil on 404 (#318).
    public func fetchBuildingThumb(osmId: Int) async throws -> Data? {
        var req = URLRequest(url: baseURL.appendingPathComponent("buildings/\(osmId)/thumb"))
        req.httpMethod = "GET"
        let (data, response) = try await session.data(for: req)
        guard let http = response as? HTTPURLResponse else { return nil }
        if http.statusCode == 404 { return nil }
        guard (200..<300).contains(http.statusCode) else { throw ServerError.http(http.statusCode) }
        return data
    }

    // GET /parts/{id}/thumb — rendered preview image; nil on 404 (#344, same push model as
    // building thumbnails — Unity renders and PUTs one after import).
    public func fetchPartThumb(partId: String) async throws -> Data? {
        var req = URLRequest(url: baseURL.appendingPathComponent("parts/\(partId)/thumb"))
        req.httpMethod = "GET"
        let (data, response) = try await session.data(for: req)
        guard let http = response as? HTTPURLResponse else { return nil }
        if http.statusCode == 404 { return nil }
        guard (200..<300).contains(http.statusCode) else { throw ServerError.http(http.statusCode) }
        return data
    }

    // GET /parts/{id}/glb — download a part's binary GLB; nil on 404.
    public func getPartGlb(partId: String) async throws -> Data? {
        var req = URLRequest(url: baseURL.appendingPathComponent("parts/\(partId)/glb"))
        req.httpMethod = "GET"
        let (data, response) = try await session.data(for: req)
        guard let http = response as? HTTPURLResponse else { return nil }
        if http.statusCode == 404 { return nil }
        guard (200..<300).contains(http.statusCode) else { throw ServerError.http(http.statusCode) }
        return data
    }

    // PUT /parts/{id}/glb — upload a GLB binary via multipart/form-data.
    public func uploadPartGlb(partId: String, data glbData: Data) async throws {
        let boundary = "Boundary-FacadeCanvas"
        var req = URLRequest(url: baseURL.appendingPathComponent("parts/\(partId)/glb"))
        req.httpMethod = "PUT"
        req.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        var body = Data()
        func s(_ str: String) { body.append(contentsOf: str.utf8) }
        s("--\(boundary)\r\n")
        s("Content-Disposition: form-data; name=\"file\"; filename=\"\(partId).glb\"\r\n")
        s("Content-Type: model/gltf-binary\r\n\r\n")
        body.append(glbData)
        s("\r\n--\(boundary)--\r\n")
        req.httpBody = body
        let (_, response) = try await session.data(for: req)
        guard let http = response as? HTTPURLResponse else { throw ServerError.emptyBody }
        guard (200..<300).contains(http.statusCode) else { throw ServerError.http(http.statusCode) }
    }

    // --- transport ---------------------------------------------------------

    private func get<T: Decodable>(_ path: String) async throws -> T {
        var req = URLRequest(url: baseURL.appendingPathComponent(path))
        req.httpMethod = "GET"
        return try await send(req)
    }

    private func post<B: Encodable, T: Decodable>(_ path: String, body: B) async throws -> T {
        var req = URLRequest(url: baseURL.appendingPathComponent(path))
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try encoder.encode(body)
        return try await send(req)
    }

    private func send<T: Decodable>(_ req: URLRequest) async throws -> T {
        let (data, response) = try await session.data(for: req)
        guard let http = response as? HTTPURLResponse else { throw ServerError.emptyBody }
        guard (200..<300).contains(http.statusCode) else { throw ServerError.http(http.statusCode) }
        return try decoder.decode(T.self, from: data)
    }
}
